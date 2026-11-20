// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;

namespace RoslynBridge.Features.Edit.CodeActions;

/// <param name="ActionIndex">Picks an action from the listing at once.</param>
/// <param name="All">Also lists the suppress and configure actions.</param>
internal sealed record CodeActionsRequest(
    CodeActionScope Scope,
    int? ActionIndex,
    bool All,
    bool Apply,
    Paging Page
) : EditRequest(Apply, Page)
{
    public override LspAction Action => LspAction.CodeActions;

    public static Result<CodeActionsRequest> From(
        Result<CodeActionScope> scope,
        int? actionIndex,
        bool all,
        bool apply,
        Paging page
    ) =>
        scope switch
        {
            ToolError error => error,
            _ when apply && actionIndex is null => ToolError.InvalidRequest(
                "Choose actionIndex before applying a code action."
            ),
            CodeActionScope found => new CodeActionsRequest(found, actionIndex, all, apply, page),
        };

    public override CodeActionsRequest WithFiles(FileResolver resolve) =>
        this with
        {
            Scope = Scope.WithFile(resolve),
        };
}

/// <summary>
/// Where the listed code actions apply.
/// </summary>
internal closed record CodeActionScope
{
    /// <summary>
    /// The fixes of <paramref name="diagnostic"/>, or else a selection from the target to the optional end.
    /// </summary>
    public static Result<CodeActionScope> From(
        string? diagnostic,
        string? symbol,
        string? file,
        int? line,
        int? character,
        int? endLine,
        int? endCharacter
    )
    {
        if (diagnostic is not null)
        {
            return
                (symbol, file, line, character, endLine, endCharacter)
                    is (null, { } path, null or > 0, null, null, null)
                ? new DiagnosticFixes(diagnostic, path, line)
                : ToolError.InvalidRequest(
                    "Pass diagnostic with a file and an optional line, without a symbol or character."
                );
        }

        var target = SymbolTarget.From(symbol, file, line, character);
        return target.IsError(out var error, out var start)
            ? error
            : (start, endLine, endCharacter) switch
            {
                (_, null, null) => new Selection(start, End: null),
                (NamedSymbol, _, _) => ToolError.InvalidRequest(
                    "A selection end needs a file with a line and character, not a symbol."
                ),
                (_, null, _) or (_, _, null) => ToolError.InvalidRequest(
                    "Pass both endLine and endCharacter."
                ),
                (SymbolAt { Position: var first } at, int last and > 0, int column and > 0)
                    when (last, column).CompareTo((first.Line, first.Character)) >= 0 =>
                    new Selection(at, new SourcePosition(last, column)),
                _ => ToolError.InvalidRequest("The selection end must follow its start."),
            };
    }

    public CodeActionScope WithFile(FileResolver resolve) =>
        this switch
        {
            Selection selection => selection with { Start = selection.Start.WithFile(resolve) },
            DiagnosticFixes fixes => fixes with { File = resolve(fixes.File) },
        };
}

/// <param name="End">The one-based end of the selection, which is the start without one.</param>
internal sealed record Selection(SymbolTarget Start, SourcePosition? End) : CodeActionScope;

/// <param name="Line">Picks the first occurrence on this one-based line instead of in the file.</param>
internal sealed record DiagnosticFixes(string Code, string File, int? Line) : CodeActionScope;
