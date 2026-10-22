// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

internal sealed partial class LspTool
{
    private const int MaximumListedFiles = 20;

    private static readonly SearchValues<char> _globCharacters = SearchValues.Create("*?[{");

    private async Task<Result<ResolvedRequest>> ResolveFilesAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var names = (
            request.Action is LspAction.Bulk
                ? request.Queries.Select(query => query.File)
                : [request.File]
        )
            .OfType<string>()
            .Where(name => IsMissingFile(request.Action, name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
        {
            return new ResolvedRequest(request, []);
        }

        await session.EnsureStartedAsync(cancellationToken);
        var known = await session.WorkspaceFilesAsync(cancellationToken);
        // rename_file may move any file, while every other tool reads C# files
        var candidates =
            request.Action is LspAction.RenameFile
                ? known
                : known.Where(RoslynSession.IsCSharpFile);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var matches = paths.RelativePathsEndingWith(candidates, name);
            if (matches.Count > 1)
            {
                return AmbiguousFile(name, matches);
            }

            if (matches.Count == 1)
            {
                resolved[name] = matches[0];
            }
        }

        return new ResolvedRequest(
            request with
            {
                File = Resolved(request.File),
                Queries =
                [
                    .. request.Queries.Select(query => query with { File = Resolved(query.File) }),
                ],
            },
            [.. resolved.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
        );

        string? Resolved(string? file) =>
            file is not null && resolved.TryGetValue(file, out var path) ? path : file;
    }

    // a glob selects files without naming one
    private bool IsMissingFile(LspAction action, string name) =>
        !string.IsNullOrWhiteSpace(name)
        && (action is not LspAction.Diagnostics || name.AsSpan().IndexOfAny(_globCharacters) < 0)
        && !File.Exists(Path.Combine(paths.Root, name));

    private static ToolError AmbiguousFile(string name, List<string> matches) =>
        new(
            ErrorCodes.AmbiguousFile,
            $"'{name}' matches {matches.Count} files. Send more of the path. Matches: "
                + string.Join(", ", matches.Take(MaximumListedFiles))
                + (matches.Count > MaximumListedFiles ? ", ..." : string.Empty)
        );

    private readonly record struct ResolvedRequest(LspRequest Request, IReadOnlyList<string> Files);
}
