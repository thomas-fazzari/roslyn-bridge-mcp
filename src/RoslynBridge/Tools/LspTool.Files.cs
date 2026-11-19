// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using System.Collections.ObjectModel;
using RoslynBridge.Requests;

namespace RoslynBridge.Tools;

internal sealed partial class LspTool
{
    private const int MaximumListedFiles = 20;

    private static readonly SearchValues<char> _globCharacters = SearchValues.Create("*?[{");

    private async Task<Result<ResolvedRequest>> ResolveFilesAsync(
        ToolRequest request,
        CancellationToken cancellationToken
    )
    {
        var scope = request.Policy.Files;
        var names = request
            .FileNames.OfType<string>()
            .Where(name => IsMissingFile(scope, name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return new ResolvedRequest(request, [], ReadOnlyDictionary<string, ToolError>.Empty);

        if (await session.EnsureStartedAsync(cancellationToken) is ToolError startError)
            return startError;

        var files = await session.WorkspaceFilesAsync(cancellationToken);
        if (files.IsError(out var filesError, out var known))
            return filesError;

        // rename_file may move any file, while every other tool reads C# files
        var candidates =
            scope is FileScope.AnyFile ? known : known.Where(WorkspacePaths.IsCSharpFile);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new Dictionary<string, ToolError>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var matches = paths.RelativePathsEndingWith(candidates, name);
            if (matches.Count > 1)
            {
                var error = AmbiguousFile(name, matches);
                if (request is not BulkRequest)
                    return error;

                ambiguous[name] = error;
            }
            else if (matches.Count == 1)
            {
                resolved[name] = matches[0];
            }
        }

        return new ResolvedRequest(
            request.WithFiles(Resolved),
            [.. resolved.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            ambiguous
        );

        string? Resolved(string? file) =>
            file is not null && resolved.TryGetValue(file, out var path) ? path : file;
    }

    // a glob selects files without naming one
    private bool IsMissingFile(FileScope scope, string name) =>
        !string.IsNullOrWhiteSpace(name)
        && (scope is not FileScope.CSharpOrGlob || name.AsSpan().IndexOfAny(_globCharacters) < 0)
        && !File.Exists(Path.Combine(paths.Root, name));

    private static ToolError AmbiguousFile(string name, List<string> matches) =>
        new(
            ErrorCodes.AmbiguousFile,
            $"'{name}' matches {matches.Count} files. Send more of the path. Matches: "
                + string.Join(", ", matches.Take(MaximumListedFiles))
                + (matches.Count > MaximumListedFiles ? ", ..." : string.Empty)
        );

    /// <param name="FileErrors">The errors of the bulk query files that match several files, by name.</param>
    private readonly record struct ResolvedRequest(
        ToolRequest Request,
        IReadOnlyList<string> Files,
        IReadOnlyDictionary<string, ToolError> FileErrors
    );
}
