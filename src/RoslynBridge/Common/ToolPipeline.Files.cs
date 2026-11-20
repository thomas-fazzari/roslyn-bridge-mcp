// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using RoslynBridge.Requests;

namespace RoslynBridge.Common;

internal sealed partial class ToolPipeline
{
    private const int MaximumListedFiles = 20;

    private static readonly SearchValues<char> _globCharacters = SearchValues.Create("*?[{");

    private async Task<Result<ResolvedRequest>> ResolveFilesAsync(
        ToolRequest request,
        CancellationToken cancellationToken
    )
    {
        var scope = request.Policy.Files;
        var named = new List<string>();
        request.WithFiles(file =>
        {
            if (file is not null)
                named.Add(file);

            return file;
        });
        var names = named
            .Where(name => IsMissingFile(scope, name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return new ResolvedRequest(request, []);

        if (await session.EnsureStartedAsync(cancellationToken) is ToolError startError)
            return startError;

        var files = await session.WorkspaceFilesAsync(cancellationToken);
        if (files.IsError(out var filesError, out var known))
            return filesError;

        // rename_file may move any file, while every other tool reads C# files
        var candidates =
            scope is FileScope.AnyFile ? known : known.Where(WorkspacePaths.IsCSharpFile);
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new List<(string Name, ToolError Error)>();

        foreach (var name in names)
        {
            switch (paths.RelativePathsEndingWith(candidates, name))
            {
                case [var match]:
                    resolved[name] = match;
                    break;
                case { Count: > 1 } matches:
                    ambiguous.Add((name, AmbiguousFile(name, matches)));
                    break;
            }
        }

        return request
            .WithFiles(Resolved)
            .WithFileErrors(ambiguous)
            .Map(withErrors => new ResolvedRequest(
                withErrors,
                [.. resolved.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]
            ));

        [return: NotNullIfNotNull(nameof(file))]
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

    private readonly record struct ResolvedRequest(
        ToolRequest Request,
        IReadOnlyList<string> Files
    );
}
