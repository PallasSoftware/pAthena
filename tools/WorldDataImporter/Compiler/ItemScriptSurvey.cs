using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Rathena.Syntax;

namespace Athena.WorldCompiler.Generation;

// Counts which statements, commands, functions and variable scopes the item-family scripts really use, so the lowering
// catalog is driven by the pinned data instead of guesses.
internal static class ItemScriptSurvey
{
    internal static SortedDictionary<string, int> Run(Func<string, string> readFile)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var builtins = RathenaBuiltins.Names(readFile("src/map/script.cpp"));
        void Count(string key) { counts[key] = counts.GetValueOrDefault(key) + 1; }

        foreach (var file in RathenaNameCollector.ScriptBearingFiles)
        {
            foreach (var script in RathenaNameCollector.ScriptsOf(RathenaYaml.Parse(readFile(file), file)))
            {
                Count("scripts");
                var unit = new RathenaParser(script, file, 1, builtins).ParseCompilationUnit();
                foreach (var diagnostic in unit.Diagnostics) { Count("diag:" + diagnostic.Code); if (counts.GetValueOrDefault("diag:" + diagnostic.Code) <= 6) Console.Error.WriteLine($"{diagnostic.Code} {diagnostic.Message}\n--- {script.Replace("\n", "\n    ")}"); }
                if (unit.Diagnostics.Count > 0) Count("scripts-with-diagnostics");
                foreach (var statement in unit.Statements) Walk(statement, Count);
            }
        }
        return counts;
    }

    private static void Walk(StatementSyntax statement, Action<string> count)
    {
        count("stmt:" + statement.GetType().Name.Replace("StatementSyntax", ""));
        switch (statement)
        {
            case BlockStatementSyntax block: foreach (var child in block.Statements) Walk(child, count); break;
            case IfStatementSyntax conditional:
                Expression(conditional.Condition, count); Walk(conditional.Then, count);
                if (conditional.Else is not null) Walk(conditional.Else, count);
                break;
            case WhileStatementSyntax loop: Expression(loop.Condition, count); Walk(loop.Body, count); break;
            case ForStatementSyntax loop: Walk(loop.Body, count); break;
            case SwitchStatementSyntax selection:
                Expression(selection.Expression, count);
                foreach (var clause in selection.Clauses) foreach (var child in clause.Statements) Walk(child, count);
                break;
            case ExpressionStatementSyntax expression: Expression(expression.Expression, count); break;
            case CommandStatementSyntax command:
                count("cmd:" + command.Name.ToLowerInvariant());
                foreach (var argument in command.Arguments) Expression(argument, count);
                break;
        }
    }

    private static void Expression(ExpressionSyntax expression, Action<string> count)
    {
        switch (expression)
        {
            case CallExpressionSyntax call:
                count("fn:" + (call.Target is IdentifierExpressionSyntax id ? id.Name.ToLowerInvariant() : "<dynamic>"));
                foreach (var argument in call.Arguments) Expression(argument, count);
                break;
            case BinaryExpressionSyntax binary: Expression(binary.Left, count); Expression(binary.Right, count); break;
            case UnaryExpressionSyntax unary: Expression(unary.Operand, count); break;
            case AssignmentExpressionSyntax assignment: count("assign:" + assignment.Operator); Expression(assignment.Target, count); Expression(assignment.Value, count); break;
            case ConditionalExpressionSyntax conditional: count("expr:conditional"); Expression(conditional.Condition, count); Expression(conditional.WhenTrue, count); Expression(conditional.WhenFalse, count); break;
            case IndexExpressionSyntax index: count("expr:index"); Expression(index.Target, count); break;
            case PostfixExpressionSyntax postfix: count("expr:postfix"); Expression(postfix.Operand, count); break;
            case VariableExpressionSyntax variable: count("var:" + variable.Scope); break;
        }
    }
}
