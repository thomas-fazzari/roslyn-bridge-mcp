// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Requests;
using RoslynBridge.Symbols;

namespace RoslynBridge.Features.Navigate;

internal sealed record NavigateRequest(
    LspAction Action,
    SymbolTarget Target,
    bool Inherited,
    bool Context,
    Paging Page
) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy => new(Action);

    // source and members read the declaration that contains a line, so they need no character
    public static Result<NavigateRequest> From(
        LspAction action,
        (string? Symbol, string? File, int? Line, int? Character) target,
        bool inherited,
        bool context,
        Paging page
    )
    {
        if (inherited && action is not LspAction.Members)
            return ToolError.InvalidRequest("inherited only applies to members.");

        var (symbol, file, line, character) = target;
        var found = action is LspAction.Source or LspAction.Members
            ? SymbolTarget.Declaration(symbol, file, line, character)
            : SymbolTarget.From(symbol, file, line, character);
        return found.Map(valid => new NavigateRequest(action, valid, inherited, context, page));
    }

    public bool IsAccess => Action is LspAction.Reads or LspAction.Writes;

    public bool IsHierarchy =>
        Action
            is LspAction.Callers
                or LspAction.Callees
                or LspAction.Supertypes
                or LspAction.Subtypes;

    // Hierarchy and impact start from the declaration, and members and type hierarchies take a type
    public SymbolLookup Lookup =>
        new(
            PrefersTypes: Action is LspAction.Members or LspAction.Subtypes or LspAction.Supertypes,
            ToDeclaration: IsHierarchy || Action is LspAction.Impact
        );

    public override NavigateRequest WithFiles(FileResolver resolve) =>
        this with
        {
            Target = Target.WithFile(resolve),
        };
}
