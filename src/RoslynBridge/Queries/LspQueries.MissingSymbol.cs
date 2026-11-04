// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    // Each candidate costs a hover request
    private const int MaximumNearbyNames = 4;

    /// <summary>
    /// The error for a position that touches no symbol, such as white space or punctuation beside a name.
    /// It names the nearest symbol on the line. Null when the position touches a symbol.
    /// </summary>
    /// <param name="parameters">The textDocument and zero-based position of a request.</param>
    public async Task<ToolError?> MissingSymbolAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        if (await HasSymbolAsync(parameters, cancellationToken))
        {
            return null;
        }

        var uri = parameters["textDocument"]!["uri"]!.GetValue<string>();
        var position = parameters["position"].Read<LspPosition>();
        var file = paths.TryGetWorkspaceFile(uri);
        var requested = position.OneBased;
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"No symbol at {requested.Line}:{requested.Character} of {(file is null ? uri : paths.RelativePath(file))}."
        );
        var nearest = await NearestSymbolAsync(uri, file, position, cancellationToken);
        var hint = nearest is var (name, found)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" The nearest symbol on the line is {name} at {found.Line}:{found.Character}."
            )
            : " Check the line and character, which start at 1.";
        return new ToolError(ErrorCodes.SymbolNotFound, message + hint);
    }

    private async Task<bool> HasSymbolAsync(
        JsonObject parameters,
        CancellationToken cancellationToken
    ) =>
        (await session.RequestAsync(LspMethods.TextDocumentHover, parameters, cancellationToken))
            .ToHoverText()
            .Length > 0;

    private async Task<(string Name, SourcePosition Position)?> NearestSymbolAsync(
        string uri,
        string? file,
        LspPosition position,
        CancellationToken cancellationToken
    )
    {
        var lines = SourceLines.TryReadLines(file);
        if (lines is null || position.Line >= lines.Length)
        {
            return null;
        }

        var line = lines[position.Line];
        var candidates = Names(line)
            .OrderBy(name => Distance(name, position.Character))
            .Take(MaximumNearbyNames);
        foreach (var (start, length) in candidates)
        {
            var candidate = LspParameters.Document(uri);
            candidate["position"] = new JsonObject
            {
                ["line"] = position.Line,
                ["character"] = start,
            };
            if (await HasSymbolAsync(candidate, cancellationToken))
            {
                return (
                    line.Substring(start, length),
                    new LspPosition(position.Line, start).OneBased
                );
            }
        }

        return null;
    }

    // Keywords and words in comments also match, and the hover check leaves them out
    private static IEnumerable<(int Start, int Length)> Names(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (!IsNameStart(line[index]))
            {
                continue;
            }

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
        {
            return name.Start - character;
        }

        var last = name.Start + name.Length - 1;
        return character > last ? character - last : 0;
    }

    private static bool IsNameStart(char character) => char.IsLetter(character) || character is '_';

    private static bool IsNamePart(char character) =>
        char.IsLetterOrDigit(character) || character is '_';
}
