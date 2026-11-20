// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.FileSystemGlobbing;

namespace RoslynBridge.Workspace;

internal sealed class FileSelection(WorkspacePaths paths)
{
    internal const int MaximumFiles = 1000;

    /// <summary>
    /// Selects the files of <paramref name="file"/>, or fails when they are more than <see cref="MaximumFiles"/>.
    /// </summary>
    /// <param name="tool">The tool named in the error, such as Diagnostics.</param>
    public Result<WorkspaceFilePath[]> SelectAtMost(
        string? file,
        string tool,
        CancellationToken cancellationToken
    )
    {
        if (SelectFiles(file, cancellationToken).IsError(out var error, out var selected))
            return error;

        var files = selected.Take(MaximumFiles + 1).ToArray();
        return files.Length > MaximumFiles
            ? ToolError.InvalidRequest(
                $"{tool} accepts at most {MaximumFiles} files. Use a narrower glob."
            )
            : files;
    }

    public Result<List<WorkspaceFilePath>> SelectFiles(
        string? file,
        CancellationToken cancellationToken
    )
    {
        var patterns = file is null or "*" ? ["**/*.cs"] : ExpandBraces(file);
        if (patterns.TrueForAll(pattern => pattern.IndexOfAny(['*', '?']) < 0))
        {
            return patterns
                .Select(paths.Resolve)
                .All()
                .Map(resolved => resolved.Distinct().ToList());
        }

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(patterns);
        return paths
            .EnumerateFiles(cancellationToken)
            .Map(files =>
            {
                var byFullPath = files.ToDictionary(
                    file => file.Path.FullPath,
                    file => file.Path,
                    StringComparer.Ordinal
                );
                return matcher
                    .Match(paths.Root, byFullPath.Keys)
                    .Files.Select(match =>
                        byFullPath.GetValueOrDefault(Path.GetFullPath(match.Path, paths.Root))
                    )
                    .OfType<WorkspaceFilePath>()
                    .Where(WorkspacePaths.IsCSharpFile)
                    .Order(WorkspaceFilePath.Order)
                    .ToList();
            });
    }

    // The matcher has no brace alternatives, so `src/{A,B}.cs` becomes one pattern per alternative
    private static List<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
            return [pattern];

        var depth = 0;
        var start = open + 1;
        var alternatives = new List<string>();
        for (var index = start; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '{':
                    depth++;
                    break;
                case ',' when depth == 0:
                    alternatives.Add(pattern[start..index]);
                    start = index + 1;
                    break;
                case '}' when depth == 0:
                    alternatives.Add(pattern[start..index]);
                    return
                    [
                        .. alternatives.SelectMany(alternative =>
                            ExpandBraces(pattern[..open] + alternative + pattern[(index + 1)..])
                        ),
                    ];
                case '}':
                    depth--;
                    break;
            }
        }

        return [pattern];
    }
}
