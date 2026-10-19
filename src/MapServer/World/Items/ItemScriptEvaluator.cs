namespace Athena.Net.MapServer.World.Items;

// What a lowered item script may do to the character using it (Feature 16, architecture decision 1). Implemented by the
// session that owns the character's authoritative state; every member routes into the SAME mechanism the rest of the server
// already uses for that effect (CharacterHealService for HP/SP, CharacterStatusEffectState for statuses) - never a second one.
public interface IItemEffectHost
{
    // pc_itemheal (pc.cpp:10695-10786): a flat heal scaled by the user's VIT/INT and recovery skills.
    Task ItemHealAsync(int hp, int sp, CancellationToken cancellationToken);

    // pc_percentheal (pc.cpp:10792-10823) for non-negative rates: a heal of rate% of MaxHP/MaxSP.
    Task PercentHealAsync(int hpRate, int spRate, CancellationToken cancellationToken);

    // BUILDIN_FUNC(heal) -> status_heal(sd, hp, sp, 1): an unscaled flat heal.
    Task HealAsync(int hp, int sp, CancellationToken cancellationToken);

    // BUILDIN_FUNC(sc_start) (script.cpp:12401-12465) -> status_change_start.
    Task StartStatusAsync(ushort statusId, int durationMilliseconds, int val1, int val2, int val3, CancellationToken cancellationToken);

    // BUILDIN_FUNC(sc_end) (script.cpp:12471-12516) -> status_change_end.
    Task EndStatusAsync(ushort statusId, CancellationToken cancellationToken);
}

// One resolved command of an item script: its arguments are already evaluated (rand rolled, constants resolved).
public sealed record ItemEffect(string Command, IReadOnlyList<long> Arguments);

// Either every effect of the script (in source order) or the reason it cannot be executed. Built BEFORE the item is consumed,
// so an item whose script this evaluator cannot run in full is rejected without mutation instead of losing part of its effect.
public sealed record ItemScriptPlan(IReadOnlyList<ItemEffect> Effects, string? Unsupported)
{
    public bool Executable => Unsupported is null;
}

// The generic item-script interpreter over the typed IR stored in the item pack. Commands are dispatched by name to one fixed
// handler each, so every item declaring a supported command runs through the same code - nothing here knows item identity.
// Supported today: straight-line Block/Command scripts whose arguments are Int/Const/unary/arithmetic/rand() expressions.
// Anything else (control flow, character-dependent parameters, Deferred F18/F20 constructs, uncataloged commands) yields an
// actionable Unsupported diagnostic naming the construct and its owner.
public static class ItemScriptEvaluator
{
    private sealed class UnsupportedException(string reason) : Exception(reason);

    // Run is null for a grant: its items are created in the same durable operation that consumes the used item, not afterwards.
    private sealed record Handler(int MinArguments, int MaxArguments, Func<long[], string?> Validate, Func<IItemEffectHost, long[], CancellationToken, Task>? Run)
    {
        public Handler(int arguments, Func<long[], string?> validate, Func<IItemEffectHost, long[], CancellationToken, Task>? run) : this(arguments, arguments, validate, run) { }
    }

    private static string? NonNegative(long[] a) => a.Any(v => v < 0 || v > int.MaxValue) ? "negative-or-out-of-range-amount" : null;

    // Only the basic forms: the optional rate/flag/unit-id arguments (rate < 10000 rolls against status resistance) and a
    // zero/negative duration (skill_db duration lookup, infinite statuses) are not modeled by the status system yet.
    private static string? StatusStart(long[] a) =>
        a[0] is < 0 or > ushort.MaxValue ? "status-id-out-of-range"
        : a[1] is <= 0 or > int.MaxValue ? "non-positive-status-duration"
        : null;

    private static readonly Dictionary<string, Handler> Handlers = new(StringComparer.Ordinal)
    {
        // script.cpp:6038-6056 BUILDIN_FUNC(itemheal) hp,sp -> pc_itemheal. Negative amounts take status_damage (not in this slice).
        ["itemheal"] = new(2, NonNegative, (host, a, ct) => host.ItemHealAsync((int)a[0], (int)a[1], ct)),
        // script.cpp:6062-6093 BUILDIN_FUNC(percentheal) hp,sp -> pc_percentheal, which caps each rate at 100 (pc.cpp:10796-10800).
        ["percentheal"] = new(2, NonNegative, (host, a, ct) => host.PercentHealAsync((int)Math.Min(a[0], 100), (int)Math.Min(a[1], 100), ct)),
        ["heal"] = new(2, NonNegative, (host, a, ct) => host.HealAsync((int)a[0], (int)a[1], ct)),
        // script.cpp:12401-12465: sc_start type,tick,val1 / sc_start2 ...,val2 / sc_start4 ...,val2,val3,val4.
        ["sc_start"] = new(3, StatusStart, (host, a, ct) => host.StartStatusAsync((ushort)a[0], (int)a[1], (int)a[2], 0, 0, ct)),
        ["sc_start2"] = new(4, StatusStart, (host, a, ct) => host.StartStatusAsync((ushort)a[0], (int)a[1], (int)a[2], (int)a[3], 0, ct)),
        ["sc_start4"] = new(6, a => StatusStart(a) ?? (a[5] != 0 ? "sc_start4-val4-not-modeled" : null),
            (host, a, ct) => host.StartStatusAsync((ushort)a[0], (int)a[1], (int)a[2], (int)a[3], (int)a[4], ct)),
        // script.cpp:12471-12516: sc_end type ends that status when active; SC_NONE (-1) is status_change_clear, not modeled here.
        ["sc_end"] = new(1, a => a[0] is < 0 or > ushort.MaxValue ? "status-id-out-of-range" : null, (host, a, ct) => host.EndStatusAsync((ushort)a[0], ct)),
        // Presentation only (clif_specialeffect -> 0x01F3): no server state. Protocol-pending-capture, exactly like the NPC host's
        // SpecialEffectAsync - nothing is fabricated on the wire without a stock-iRO capture.
        ["specialeffect2"] = new(0, int.MaxValue, _ => null, (_, _, _) => Task.CompletedTask),
        // script.cpp BUILDIN_FUNC(getitem) id,amount: a non-positive amount gives nothing (it is still a successful command).
        ["getitem"] = new(2, a => a[0] is <= 0 or > int.MaxValue ? "item-id-out-of-range" : a[1] > ushort.MaxValue ? "amount-out-of-range" : null, null),
        // script.cpp:23225-23243 BUILDIN_FUNC(getgroupitem) group_id{,identify} -> ItemGroupDatabase::pc_get_itemgroup.
        ["getgroupitem"] = new(1, 2, a => a[0] is < 0 or > ushort.MaxValue ? "group-id-out-of-range" : null, null),
    };

    public static bool IsGrant(ItemEffect effect) => Handlers[effect.Command].Run is null;

    public static IReadOnlyCollection<string> SupportedCommands => Handlers.Keys;

    // `random(min, max)` returns a value in [min, max] inclusive (common/random.hpp rnd_value).
    public static ItemScriptPlan Plan(PackScript? script, Func<long, long, long> random)
    {
        if (script is null) return new([], "no-script");
        if (script.Support == PackScriptSupport.Deferred)
            return new([], $"deferred:{script.DeferredOwner}:{script.DeferredConstruct}");
        if (script.Program is null) return new([], "no-program");
        try
        {
            var effects = new List<ItemEffect>();
            Statement(script.Program, effects, random);
            return new(effects, null);
        }
        catch (UnsupportedException unsupported)
        {
            return new([], unsupported.Message);
        }
    }

    public static async Task ExecuteAsync(ItemScriptPlan plan, IItemEffectHost host, CancellationToken cancellationToken)
    {
        if (!plan.Executable) throw new InvalidOperationException($"Item script is not executable: {plan.Unsupported}");
        foreach (var effect in plan.Effects)
            if (Handlers[effect.Command].Run is { } run) await run(host, [.. effect.Arguments], cancellationToken);
    }

    private static void Statement(IrNode node, List<ItemEffect> effects, Func<long, long, long> random)
    {
        switch (node.Kind)
        {
            case IrKind.Block:
                foreach (var child in node.Items) Statement(child, effects, random);
                return;
            // A function called as a statement (`getgroupitem(IG_BlueBox);`) runs exactly like its command spelling; its value is unused.
            case IrKind.ExprStmt when node.Items is [{ Kind: IrKind.Call } call] && Handlers.ContainsKey(call.Text!):
                Statement(call with { Kind = IrKind.Command }, effects, random);
                return;
            case IrKind.Command:
                var name = node.Text!;
                if (!Handlers.TryGetValue(name, out var handler))
                    throw new UnsupportedException($"command:{name} owner:{ItemScriptCatalog.OwnerOf(name)}");
                if (node.Items.Count < handler.MinArguments || node.Items.Count > handler.MaxArguments)
                    throw new UnsupportedException($"command:{name} arguments:{node.Items.Count}");
                var arguments = node.Items.Select(argument => Expression(argument, random)).ToArray();
                if (handler.Validate(arguments) is { } invalid) throw new UnsupportedException($"command:{name} {invalid}");
                effects.Add(new(name, arguments));
                return;
            default:
                // Control flow and assignments need the general script runtime this slice does not model yet.
                throw new UnsupportedException($"statement:{node.Kind}");
        }
    }

    private static long Expression(IrNode node, Func<long, long, long> random)
    {
        switch (node.Kind)
        {
            case IrKind.Int:
            case IrKind.Const:
                return node.Number;
            case IrKind.Unary when (IrOp)node.Number == IrOp.Neg:
                return -Expression(node.Items[0], random);
            case IrKind.Unary when (IrOp)node.Number == IrOp.Plus:
                return Expression(node.Items[0], random);
            case IrKind.Binary when (IrOp)node.Number is IrOp.Add or IrOp.Sub or IrOp.Mul or IrOp.Div or IrOp.Mod:
            {
                var left = Expression(node.Items[0], random);
                var right = Expression(node.Items[1], random);
                // script.cpp op_2num: division/modulo by zero is a script error that ends the script.
                if (right == 0 && (IrOp)node.Number is IrOp.Div or IrOp.Mod) throw new UnsupportedException("division-by-zero");
                return (IrOp)node.Number switch
                {
                    IrOp.Add => left + right, IrOp.Sub => left - right, IrOp.Mul => left * right,
                    IrOp.Div => left / right, _ => left % right,
                };
            }
            case IrKind.Call when node.Text == "rand" && node.Items.Count is 1 or 2:
                return Rand(node.Items.Select(argument => Expression(argument, random)).ToArray(), random);
            default:
                throw new UnsupportedException(node.Kind == IrKind.Call ? $"function:{node.Text}" : $"expression:{node.Kind}");
        }
    }

    // script.cpp:5579-5622 BUILDIN_FUNC(rand): rand(max) is [0, max-1]; rand(min,max) is [min, max] inclusive (rnd_value swaps
    // reversed bounds). A range with no randomness (max-1 < 1, or min == max) is a script error that ends the script.
    private static long Rand(long[] arguments, Func<long, long, long> random)
    {
        if (arguments.Length == 1)
        {
            var maximum = arguments[0] - 1;
            if (maximum < 1) throw new UnsupportedException("rand-range-too-small");
            return random(0, maximum);
        }
        if (arguments[0] == arguments[1]) throw new UnsupportedException("rand-equal-bounds");
        return random(Math.Min(arguments[0], arguments[1]), Math.Max(arguments[0], arguments[1]));
    }
}
