namespace Athena.Net.MapServer.World.Items;

// Typed item-effect IR (Feature 16, decision D4). The importer lowers rAthena item/combo/random-option scripts - parsed by the
// existing RathenaLexer/Parser - into this tree; MapServer evaluates it through IItemEffectHost. It is deliberately a
// plain, serializable tree of one node type (kind + text + number + children) so it can be stored in the item data pack
// and later extended by Feature 20 (general script execution) without a data-model change. No gameplay logic lives here.
//
// Node layouts (children in order):
//   Block[stmts...]  If[cond, then, else?]  Switch[value, Case...]  Case[value|Empty (default), stmts...]
//   While[cond, body]  For[init|Empty, cond|Empty, step|Empty, body]  Break  Continue  Return[value?]  ExprStmt[expr]
//   Command(Text=lower-case buildin name)[args...]
//   Int(Number)  Str(Text)  Local(Text=".@name" or ".@name$")  Var(Text=name, Number=IrVarScope)  Index(Text=array name, Number=IrVarScope)[index]
//   Assign(Number=IrOp.*Set)[target Local|Var|Index, value]  Binary(Number=IrOp)[l, r]  Unary(Number=IrOp)[e]  Postfix(Number=IrOp)[target]
//   Call(Text=lower-case function name)[args...]  Conditional[cond, a, b]
//   Const(Text=name, Number=resolved value)  Param(Text=name, Number=parameter id)  Script(Text=original source)[Block]  (nested script string)
public enum IrKind : byte
{
    Empty = 0, Block, If, Switch, Case, Break, Continue, While, For, Return, ExprStmt, Command,
    Int, Str, Local, Var, Index, Assign, Binary, Unary, Postfix, Call, Conditional, Const, Param, Script,
}

public enum IrOp : byte
{
    Add, Sub, Mul, Div, Mod, Shl, Shr, BitAnd, BitOr, BitXor, LogAnd, LogOr, Eq, Ne, Lt, Gt, Le, Ge,
    Neg, Plus, Not, BitNot, PreInc, PreDec, PostInc, PostDec,
    Set, AddSet, SubSet, MulSet, DivSet, ModSet,
}

// Pinned variable prefixes (script.cpp parse_variable): everything except Local needs persistent/global variable storage,
// which is general script execution (Feature 20).
public enum IrVarScope : byte { Character, CharacterTemporary, Server, ServerTemporary, Npc, Local, Instance, Account, GlobalAccount }

public sealed record IrNode(IrKind Kind, string? Text = null, long Number = 0, IReadOnlyList<IrNode>? Children = null)
{
    public static readonly IReadOnlyList<IrNode> NoChildren = [];
    public IReadOnlyList<IrNode> Items => Children ?? NoChildren;
}

// Who executes a command. Feature 16 owns item mechanics and bonus accumulation; the other owners are dispatch hooks the
// evaluator calls through IItemEffectHost (never silently ignored).
public enum ItemScriptOwner : byte { Item = 0, SkillExecution = 1, StatusEffect = 2, OtherSystem = 3, GeneralScript = 4, ClientEffect = 5, Unknown = 255 }

[Flags]
public enum PackScriptUses : ushort
{
    None = 0,
    Bonus = 1 << 0,
    TriggeredBonus = 1 << 1,   // autobonus / autobonus2 / autobonus3
    SkillExecution = 1 << 2,   // skill, itemskill, unitskilluseid
    StatusEffect = 1 << 3,     // sc_start*, sc_end, bonus_script
    OtherSystem = 1 << 4,      // pet, mercenary, crafting, guild, ...
    GeneralScript = 1 << 5,    // callfunc, goto, labels, persistent variables, arrays outside the local scope
    ClientEffect = 1 << 6,     // hateffect, specialeffect2, skilleffect, showscript: no server state
    ItemOperation = 1 << 7,    // itemheal, getitem, rentitem, openers of the enchant/reform/laphine windows
}

public static class ItemScriptCatalog
{
    private static readonly Dictionary<string, ItemScriptOwner> Commands = new(StringComparer.OrdinalIgnoreCase);

    static ItemScriptCatalog()
    {
        void Add(ItemScriptOwner owner, params string[] names) { foreach (var name in names) Commands[name] = owner; }
        Add(ItemScriptOwner.Item, "bonus", "bonus2", "bonus3", "bonus4", "bonus5", "autobonus", "autobonus2", "autobonus3",
            "itemheal", "percentheal", "heal", "getitem", "getitem2", "getitem3", "getitembound", "getitembound2", "rentitem", "rentitem2", "rentitem3",
            "delitem", "set", "getexp", "getexp2", "laphine_synthesis", "laphine_upgrade", "item_reform", "item_enchant");
        Add(ItemScriptOwner.SkillExecution, "skill", "itemskill", "unitskilluseid");
        Add(ItemScriptOwner.StatusEffect, "sc_start", "sc_start2", "sc_start4", "sc_end", "bonus_script", "mercenary_sc_start", "active_transform");
        Add(ItemScriptOwner.ClientEffect, "hateffect", "specialeffect2", "skilleffect", "showscript", "playbgm", "announce");
        Add(ItemScriptOwner.OtherSystem, "pet", "bpet", "mercenary_create", "mercenary_heal", "homevolution", "addhomintimacy", "monster", "cooking",
            "makerune", "produce", "searchstores", "buyingstore", "setmadogear", "setfont", "transform", "guildgetexp", "charmoves", "charrename",
            "roulettebronze", "roulettesilver", "roulettegold", "warp", "input");
        Add(ItemScriptOwner.GeneralScript, "callfunc", "callsub", "goto", "setarray");
    }

    public static ItemScriptOwner OwnerOf(string command) => Commands.GetValueOrDefault(command, ItemScriptOwner.Unknown);

    public static PackScriptUses UsesOf(string command) => command.ToLowerInvariant() switch
    {
        "bonus" or "bonus2" or "bonus3" or "bonus4" or "bonus5" => PackScriptUses.Bonus,
        "autobonus" or "autobonus2" or "autobonus3" => PackScriptUses.TriggeredBonus,
        _ => OwnerOf(command) switch
        {
            ItemScriptOwner.SkillExecution => PackScriptUses.SkillExecution,
            ItemScriptOwner.StatusEffect => PackScriptUses.StatusEffect,
            ItemScriptOwner.OtherSystem => PackScriptUses.OtherSystem,
            ItemScriptOwner.GeneralScript => PackScriptUses.GeneralScript,
            ItemScriptOwner.ClientEffect => PackScriptUses.ClientEffect,
            ItemScriptOwner.Item => PackScriptUses.ItemOperation,
            _ => PackScriptUses.GeneralScript,
        },
    };
}
