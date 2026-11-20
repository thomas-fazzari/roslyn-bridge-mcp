// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Workspace;

namespace RoslynBridge.Symbols;

internal sealed class SymbolLocator(
    RoslynSession session,
    WorkspacePaths paths,
    SymbolResolver resolver
)
{
    // Each candidate costs a hover request
    private const int MaximumNearbyNames = 4;
    private const string NoNearestHint = " Check the line and character, which start at 1.";

    public async Task<Result<DocumentPosition>> PositionAsync(
        SymbolTarget target,
        SymbolLookup lookup,
        CancellationToken cancellationToken
    )
    {
        var located = await LocateAsync(target, lookup, cancellationToken);
        if (located.IsError(out var error, out var location))
            return error;

        return await DocumentPositionAsync(location, cancellationToken);
    }

    public async Task<Result<SourceLocation>> LocateAsync(
        SymbolTarget target,
        SymbolLookup lookup,
        CancellationToken cancellationToken
    ) =>
        target switch
        {
            NamedSymbol named => (
                await resolver.ResolveAsync(
                    named.Symbol,
                    named.File,
                    lookup.PrefersTypes,
                    cancellationToken
                )
            ).Map(found => found[0].Location),
            SymbolAt at when lookup.ToDeclaration => await DeclarationOfAsync(
                at.Location,
                cancellationToken
            ),
            SymbolAt at => at.Location,
            DeclarationLine => throw new UnreachableException(
                "Only source and members take a declaration line."
            ),
        };

    public async Task<Result<DocumentPosition>> DocumentPositionAsync(
        SourceLocation location,
        CancellationToken cancellationToken
    )
    {
        var (file, position) = location;
        var opened = await session.OpenDocumentAsync(file, cancellationToken);
        if (opened.IsError(out var openError, out var uri))
            return openError;

        if (paths.Resolve(file).IsError(out var pathError, out var path))
            return pathError;

        if (SourceLines.ReadLines(path).IsError(out var readError, out var lines))
            return readError;

        return position.Line > lines.Length
            ? ToolError.InvalidRequest(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Line {position.Line} is past the end of {paths.RelativePath(path)}, which has {lines.Length} lines."
                )
            )
            : new DocumentPosition(uri, LspPosition.From(position));
    }

    /// <summary>
    /// The error for a position that touches no symbol, such as white space or punctuation beside a name.
    /// It names the nearest symbol on the line. Null when the position touches a symbol.
    /// A failed Roslyn request returns the error of that request.
    /// </summary>
    public async Task<ToolError?> MissingSymbolAsync(
        DocumentPosition target,
        CancellationToken cancellationToken
    )
    {
        var symbolCheck = await HasSymbolAsync(target, cancellationToken);
        if (symbolCheck.IsError(out var error, out var hasSymbol))
            return error;

        if (hasSymbol)
            return null;

        var (uri, position) = target;
        var file = paths.TryGetWorkspaceFile(uri);
        var requested = position.OneBased;
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"No symbol at {requested.Line}:{requested.Character} of {(file is null ? uri : paths.RelativePath(file))}."
        );
        var nearest = await NearestSymbolHintAsync(uri, file, position, cancellationToken);
        return nearest.IsError(out var hintError, out var hint)
            ? hintError
            : new ToolError(ErrorCodes.SymbolNotFound, message + hint);
    }

    /// <summary>
    /// Roslyn prepares no call hierarchy at a member access such as <c>value.Method()</c>.
    /// Returns the position itself without a single workspace declaration.
    /// </summary>
    private async Task<Result<SourceLocation>> DeclarationOfAsync(
        SourceLocation location,
        CancellationToken cancellationToken
    )
    {
        var positioned = await DocumentPositionAsync(location, cancellationToken);
        if (positioned.IsError(out var error, out var parameters))
            return error;

        var response = await session.DefinitionAsync(parameters, cancellationToken);
        if (response.IsError(out var requestError, out var definition))
            return requestError;

        var found = definition.FindLocations(paths, skipGenerated: false);
        if (found.IsError(out var invalid, out var locations))
            return invalid;

        return
            locations.Positions is [var declaration]
            && paths.ResultFilePath(declaration.File) is { } declared
            ? declaration with
            {
                File = declared,
            }
            : location;
    }

    private async Task<Result<bool>> HasSymbolAsync(
        DocumentPosition position,
        CancellationToken cancellationToken
    ) => (await session.HoverAsync(position, cancellationToken)).Map(hover => hover.Length > 0);

    private async Task<Result<string>> NearestSymbolHintAsync(
        string uri,
        string? file,
        LspPosition position,
        CancellationToken cancellationToken
    )
    {
        var lines = SourceLines.TryReadLines(file);
        if (lines is null || position.Line >= lines.Length)
            return NoNearestHint;

        var line = lines[position.Line];
        var candidates = Names(line)
            .OrderBy(name => Distance(name, position.Character))
            .Take(MaximumNearbyNames);
        foreach (var (start, length) in candidates)
        {
            var candidate = new DocumentPosition(uri, new LspPosition(position.Line, start));
            var symbolCheck = await HasSymbolAsync(candidate, cancellationToken);
            if (symbolCheck.IsError(out var error, out var hasSymbol))
                return error;

            if (hasSymbol)
            {
                var found = new LspPosition(position.Line, start).OneBased;
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $" The nearest symbol on the line is {line.AsSpan(start, length)} at {found.Line}:{found.Character}."
                );
            }
        }

        return NoNearestHint;
    }

    // Keywords and words in comments also match, and the hover check leaves them out
    private static IEnumerable<(int Start, int Length)> Names(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (!IsNameStart(line[index]))
                continue;

            var start = index;
            while (index + 1 < line.Length && IsNamePart(line[index + 1]))
            {
                index++;
            }

            yield return (start, index - start + 1);
        }
    }

    private static int Distance((int Start, int Length) name, int character)
    {
        if (character < name.Start)
            return name.Start - character;

        var last = name.Start + name.Length - 1;
        return character > last ? character - last : 0;
    }

    private static bool IsNameStart(char character) => char.IsLetter(character) || character is '_';

    private static bool IsNamePart(char character) =>
        char.IsLetterOrDigit(character) || character is '_';
}
