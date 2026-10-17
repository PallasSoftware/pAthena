using Athena.Net.MapServer.World.Items;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Generation;

// Lowers one rAthena item-family script (item Script/EquipScript/UnEquipScript, combo, random-option, ... scripts) into the typed
// item-effect IR using the existing RathenaParser. Nothing is dropped: a script containing a construct the F16 evaluator cannot run
// (general control flow, persistent variables, an unknown command) is kept as source with a named blocker and its owner, never
// partially represented. Identifiers are resolved against the constants table produced by the real pinned rAthena.
internal sealed class ItemScriptLowerer(RathenaConstantTable constants, IReadOnlySet<string> builtins)
{
    private sealed class Context
    {
        public string? Blocker;
        public ItemScriptOwner BlockerOwner = ItemScriptOwner.Unknown;
        public PackScriptUses Uses;

        public void Block(string construct, ItemScriptOwner owner)
        {
            if (Blocker is not null) return;
            Blocker = construct;
            BlockerOwner = owner;
            Uses |= owner == ItemScriptOwner.GeneralScript ? PackScriptUses.GeneralScript : PackScriptUses.None;
        }
    }

    internal PackScript Lower(string source, string file)
    {
        var context = new Context();
        var program = LowerSource(source, file, context);
        if (context.Blocker is null) return new(source, PackScriptSupport.Lowered, null, ItemScriptOwner.Item, context.Uses, program);
        return new(source, PackScriptSupport.Deferred, context.Blocker, context.BlockerOwner, context.Uses, program);
    }

    private IrNode LowerSource(string source, string file, Context context)
    {
        var unit = new RathenaParser(source, file, 1, builtins).ParseCompilationUnit();
        if (unit.Diagnostics.Count > 0) context.Block("parse-error:" + unit.Diagnostics[0].Message, ItemScriptOwner.Unknown);
        return new IrNode(IrKind.Block, Children: Statements(unit.Statements, context));
    }

    private IReadOnlyList<IrNode> Statements(IEnumerable<StatementSyntax> statements, Context context)
    {
        var lowered = new List<IrNode>();
        foreach (var statement in statements)
            if (Statement(statement, context) is { } node) lowered.Add(node);
        return lowered;
    }

    private IrNode? Statement(StatementSyntax statement, Context context)
    {
        switch (statement)
        {
            case EmptyStatementSyntax:
                return null;
            case BlockStatementSyntax block:
                return new(IrKind.Block, Children: Statements(block.Statements, context));
            case IfStatementSyntax conditional:
            {
                var children = new List<IrNode> { Expression(conditional.Condition, context), Statement(conditional.Then, context) ?? Empty };
                if (conditional.Else is not null) children.Add(Statement(conditional.Else, context) ?? Empty);
                return new(IrKind.If, Children: children);
            }
            case SwitchStatementSyntax selection:
            {
                var children = new List<IrNode> { Expression(selection.Expression, context) };
                foreach (var clause in selection.Clauses)
                {
                    var clauseChildren = new List<IrNode> { clause.Value is null ? Empty : Expression(clause.Value, context) };
                    clauseChildren.AddRange(Statements(clause.Statements, context));
                    children.Add(new(IrKind.Case, Number: clause.Value is null ? 1 : 0, Children: clauseChildren));
                }
                return new(IrKind.Switch, Children: children);
            }
            case WhileStatementSyntax loop:
                return new(IrKind.While, Children: [Expression(loop.Condition, context), Statement(loop.Body, context) ?? Empty]);
            case ForStatementSyntax loop:
                return new(IrKind.For, Children:
                [
                    loop.Initializer is null ? Empty : Expression(loop.Initializer, context),
                    loop.Condition is null ? Empty : Expression(loop.Condition, context),
                    loop.Increment is null ? Empty : Expression(loop.Increment, context),
                    Statement(loop.Body, context) ?? Empty,
                ]);
            case BreakStatementSyntax:
                return new(IrKind.Break);
            case ContinueStatementSyntax:
                return new(IrKind.Continue);
            case ReturnStatementSyntax ret:
                return new(IrKind.Return, Children: ret.Expression is null ? null : [Expression(ret.Expression, context)]);
            case GotoStatementSyntax:
                context.Block("goto", ItemScriptOwner.GeneralScript);
                return null;
            case LabelStatementSyntax:
                context.Block("label", ItemScriptOwner.GeneralScript);
                return null;
            case ExpressionStatementSyntax expression:
                return new(IrKind.ExprStmt, Children: [Expression(expression.Expression, context)]);
            case CommandStatementSyntax command:
                return Command(command, context);
            default:
                context.Block("statement:" + statement.GetType().Name, ItemScriptOwner.Unknown);
                return null;
        }
    }

    private IrNode Command(CommandStatementSyntax command, Context context)
    {
        var name = command.Name.ToLowerInvariant();
        var owner = ItemScriptCatalog.OwnerOf(name);
        if (!builtins.Contains(name)) context.Block("unknown-command:" + name, ItemScriptOwner.Unknown);
        else if (owner == ItemScriptOwner.Unknown) context.Block("uncataloged-command:" + name, ItemScriptOwner.Unknown);
        else if (owner == ItemScriptOwner.GeneralScript) context.Block(name, ItemScriptOwner.GeneralScript);
        context.Uses |= ItemScriptCatalog.UsesOf(name);

        // "set var,value;" is the command spelling of an assignment.
        if (name == "set" && command.Arguments.Count == 2)
            return new(IrKind.Assign, Number: (long)IrOp.Set, Children: [Expression(command.Arguments[0], context), Expression(command.Arguments[1], context)]);

        var arguments = new List<IrNode>();
        var nestsScripts = name is "autobonus" or "autobonus2" or "autobonus3";
        foreach (var argument in command.Arguments)
        {
            if (nestsScripts && argument is LiteralExpressionSyntax { Value: string text } && text.TrimStart().StartsWith('{'))
                arguments.Add(new(IrKind.Script, Text: text, Children: [LowerSource(text, "<autobonus>", context)]));
            else arguments.Add(Expression(argument, context));
        }
        return new(IrKind.Command, Text: name, Children: arguments);
    }

    private IrNode Expression(ExpressionSyntax expression, Context context)
    {
        switch (expression)
        {
            case LiteralExpressionSyntax { Value: long number }:
                return new(IrKind.Int, Number: number);
            case LiteralExpressionSyntax { Value: string text }:
                return new(IrKind.Str, Text: text);
            case LiteralExpressionSyntax:
                context.Block("literal", ItemScriptOwner.Unknown);
                return Empty;
            case VariableExpressionSyntax variable:
                if (variable.Scope == RathenaVariableScope.Local) return new(IrKind.Local, Text: variable.Name);
                context.Block("persistent-variable:" + variable.Name, ItemScriptOwner.GeneralScript);
                return new(IrKind.Var, Text: variable.Name, Number: (long)Map(variable.Scope));
            case IdentifierExpressionSyntax identifier:
                return Identifier(identifier.Name, context);
            case UnaryExpressionSyntax unary:
                return new(IrKind.Unary, Number: (long)UnaryOp(unary.Operator), Children: [Expression(unary.Operand, context)]);
            case PostfixExpressionSyntax postfix:
                return new(IrKind.Postfix, Number: (long)(postfix.Operator == TokenKind.PlusPlus ? IrOp.PostInc : IrOp.PostDec), Children: [Expression(postfix.Operand, context)]);
            case BinaryExpressionSyntax binary:
                return new(IrKind.Binary, Number: (long)BinaryOp(binary.Operator), Children: [Expression(binary.Left, context), Expression(binary.Right, context)]);
            case AssignmentExpressionSyntax assignment:
                return new(IrKind.Assign, Number: (long)AssignOp(assignment.Operator), Children: [Expression(assignment.Target, context), Expression(assignment.Value, context)]);
            case ConditionalExpressionSyntax conditional:
                return new(IrKind.Conditional, Children: [Expression(conditional.Condition, context), Expression(conditional.WhenTrue, context), Expression(conditional.WhenFalse, context)]);
            case IndexExpressionSyntax { Target: VariableExpressionSyntax array } index:
                if (array.Scope != RathenaVariableScope.Local) context.Block("persistent-variable:" + array.Name, ItemScriptOwner.GeneralScript);
                return new(IrKind.Index, Text: array.Name, Number: (long)Map(array.Scope), Children: [Expression(index.Index, context)]);
            case CallExpressionSyntax { Target: IdentifierExpressionSyntax target } call:
            {
                var name = target.Name.ToLowerInvariant();
                if (!builtins.Contains(name)) context.Block("unknown-function:" + name, ItemScriptOwner.Unknown);
                else if (ItemScriptCatalog.OwnerOf(name) == ItemScriptOwner.GeneralScript) context.Block(name, ItemScriptOwner.GeneralScript);
                context.Uses |= ItemScriptCatalog.OwnerOf(name) == ItemScriptOwner.Unknown ? PackScriptUses.None : ItemScriptCatalog.UsesOf(name);
                return new(IrKind.Call, Text: name, Children: call.Arguments.Select(argument => Expression(argument, context)).ToArray());
            }
            default:
                context.Block("expression:" + expression.GetType().Name, ItemScriptOwner.Unknown);
                return Empty;
        }
    }

    private IrNode Identifier(string name, Context context)
    {
        if (constants.TryGetConstant(name, out var value)) return new(IrKind.Const, Text: name, Number: value);
        if (constants.IsParameter(name) && constants.TryGetParameter(name, out var parameter)) return new(IrKind.Param, Text: name, Number: parameter);
        // A bare unknown identifier is a permanent character variable in rAthena (e.g. "RouletteBronze++").
        context.Block("persistent-variable:" + name, ItemScriptOwner.GeneralScript);
        return new(IrKind.Var, Text: name, Number: (long)IrVarScope.Character);
    }

    private static readonly IrNode Empty = new(IrKind.Empty);

    private static IrVarScope Map(RathenaVariableScope scope) => scope switch
    {
        RathenaVariableScope.Character => IrVarScope.Character,
        RathenaVariableScope.CharacterTemporary => IrVarScope.CharacterTemporary,
        RathenaVariableScope.Server => IrVarScope.Server,
        RathenaVariableScope.ServerTemporary => IrVarScope.ServerTemporary,
        RathenaVariableScope.Npc => IrVarScope.Npc,
        RathenaVariableScope.Local => IrVarScope.Local,
        RathenaVariableScope.Instance => IrVarScope.Instance,
        RathenaVariableScope.Account => IrVarScope.Account,
        _ => IrVarScope.GlobalAccount,
    };

    private static IrOp BinaryOp(TokenKind kind) => kind switch
    {
        TokenKind.Plus => IrOp.Add, TokenKind.Minus => IrOp.Sub, TokenKind.Star => IrOp.Mul, TokenKind.Slash => IrOp.Div, TokenKind.Percent => IrOp.Mod,
        TokenKind.ShiftLeft => IrOp.Shl, TokenKind.ShiftRight => IrOp.Shr, TokenKind.BitAnd => IrOp.BitAnd, TokenKind.BitOr => IrOp.BitOr, TokenKind.BitXor => IrOp.BitXor,
        TokenKind.LogicalAnd => IrOp.LogAnd, TokenKind.LogicalOr => IrOp.LogOr, TokenKind.Equal => IrOp.Eq, TokenKind.NotEqual => IrOp.Ne,
        TokenKind.Less => IrOp.Lt, TokenKind.Greater => IrOp.Gt, TokenKind.LessEqual => IrOp.Le, TokenKind.GreaterEqual => IrOp.Ge,
        _ => throw new InvalidDataException($"No IR operator for {kind}."),
    };

    private static IrOp UnaryOp(TokenKind kind) => kind switch
    {
        TokenKind.Minus => IrOp.Neg, TokenKind.Plus => IrOp.Plus, TokenKind.Bang => IrOp.Not, TokenKind.Tilde => IrOp.BitNot,
        TokenKind.PlusPlus => IrOp.PreInc, TokenKind.MinusMinus => IrOp.PreDec,
        _ => throw new InvalidDataException($"No IR unary operator for {kind}."),
    };

    private static IrOp AssignOp(TokenKind kind) => kind switch
    {
        TokenKind.Assign => IrOp.Set, TokenKind.PlusAssign => IrOp.AddSet, TokenKind.MinusAssign => IrOp.SubSet,
        TokenKind.StarAssign => IrOp.MulSet, TokenKind.SlashAssign => IrOp.DivSet, TokenKind.PercentAssign => IrOp.ModSet,
        _ => throw new InvalidDataException($"No IR assignment operator for {kind}."),
    };
}
