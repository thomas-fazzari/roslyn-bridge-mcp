// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.FileSystemGlobbing;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries(
    RoslynSession session,
    WorkspacePaths paths,
    SymbolResolver resolver
)
{
    private const int MaximumDiagnosticFiles = 1000;

    public async Task<Result<ToolResult>> NavigationAsync(
        NavigationRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Inherited && request.Action is not LspAction.Members)
        {
            return ToolError.InvalidRequest("inherited only applies to members.");
        }

        if (request.Action is LspAction.Source)
        {
            return (await SourceAsync(request, cancellationToken)).AsToolResult();
        }

        if (request.Action is LspAction.Members)
        {
            return (await MembersAsync(request, cancellationToken)).AsToolResult();
        }

        if (request.Action is LspAction.Impact)
        {
            return await ImpactAsync(request, cancellationToken);
        }

        var positioned = await PositionAsync(request.Target, request.Action, cancellationToken);
        if (positioned.IsError(out var error, out var position))
        {
            return error;
        }

        var result = await NavigateAtAsync(request, position, cancellationToken);
        if (IsEmpty(result) && await MissingSymbolAsync(position, cancellationToken) is { } missing)
        {
            return missing;
        }

        return result;
    }

    public async Task<Result<SymbolList>> SymbolsAsync(
        SymbolsRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.File is null)
        {
            var query = request.Query ?? string.Empty;
            var response = await session.RequestAsync(
                LspMethods.WorkspaceSymbol,
                new JsonObject { ["query"] = query },
                cancellationToken
            );
            if (response.IsError(out var error, out var found))
            {
                return error;
            }

            // Roslyn returns fuzzy matches in file order, so exact and prefix matches come first
            return found
                .ReadArray<LspSymbol>()
                .Map(read =>
                    read.OrderBy(symbol => MatchRank(symbol.Name, query))
                        .ThenBy(symbol => symbol.Location?.Uri, StringComparer.Ordinal)
                        .ThenBy(symbol => symbol.Location?.Range?.Start)
                        .ToArray()
                        .ToSymbolList(paths, request.Page.Limit, request.Page.Offset)
                );
        }

        if (
            (await session.OpenDocumentAsync(request.File, cancellationToken)).IsError(
                out var openError,
                out var uri
            )
        )
        {
            return openError;
        }

        var symbolsResponse = await session.RequestAsync(
            LspMethods.TextDocumentDocumentSymbol,
            LspParameters.Document(uri),
            cancellationToken
        );
        if (symbolsResponse.IsError(out var symbolsError, out var documentSymbols))
        {
            return symbolsError;
        }

        var (limit, offset) = request.Page;
        return documentSymbols.ToSymbolList(paths, limit, offset);
    }

    public async Task<Result<DiagnosticReport>> DiagnosticsAsync(
        DiagnosticsRequest request,
        CancellationToken cancellationToken
    )
    {
        if (SelectFiles(request.File, cancellationToken).IsError(out var error, out var selected))
        {
            return error;
        }

        var files = selected.Take(MaximumDiagnosticFiles + 1).ToArray();
        if (files.Length > MaximumDiagnosticFiles)
        {
            return ToolError.InvalidRequest(
                $"Diagnostics accepts at most {MaximumDiagnosticFiles} files. Use a narrower glob."
            );
        }

        return await DiagnosticsAsync(files, request, cancellationToken);
    }

    /// <param name="files">Full paths of C# files.</param>
    public async Task<Result<DiagnosticReport>> DiagnosticsAsync(
        IReadOnlyCollection<string> files,
        DiagnosticsRequest request,
        CancellationToken cancellationToken
    )
    {
        if (await session.SynchronizeNewFilesAsync(files, cancellationToken) is ToolError error)
        {
            return error;
        }

        var scanned = new List<ScannedFile>(files.Count);
        foreach (var batch in files.Chunk(RoslynConnection.MaximumOpenDocuments))
        {
            if (
                (await ScanAsync(batch, cancellationToken)).IsError(
                    out var scanError,
                    out var batchFiles
                )
            )
            {
                return scanError;
            }

            scanned.AddRange(batchFiles);
        }

        return Report(scanned, request);
    }

    /// <param name="action">Decides whether the symbol resolves to a type, and whether a usage moves to its declaration.</param>
    public async Task<Result<JsonObject>> PositionAsync(
        SymbolTarget target,
        LspAction action,
        CancellationToken cancellationToken
    )
    {
        var located = await LocateAsync(target, action, cancellationToken);
        if (located.IsError(out var error, out var location))
        {
            return error;
        }

        var (file, position) = location;
        return await PositionParametersAsync(file, position, cancellationToken);
    }

    private async Task<Result<ToolResult>> NavigateAtAsync(
        NavigationRequest request,
        JsonObject parameters,
        CancellationToken cancellationToken
    )
    {
        if (request.Action.IsHierarchy)
        {
            return await HierarchyAsync(request, parameters, cancellationToken);
        }

        if (request.Action is LspAction.References || request.Action.IsAccess)
        {
            var lookup = await ReferencesAsync(parameters, cancellationToken);
            if (lookup.IsError(out var error, out var found))
            {
                return error;
            }

            if (request.Action.IsAccess)
            {
                return await AccessAsync(request, found, cancellationToken);
            }

            var references = found.Locations.Page(request.Page.Limit, request.Page.Offset);
            return request.Context ? SourceLines.Add(references, paths) : references;
        }

        var method = request.Action switch
        {
            LspAction.Definition => LspMethods.TextDocumentDefinition,
            LspAction.TypeDefinition => LspMethods.TextDocumentTypeDefinition,
            LspAction.Implementation => LspMethods.TextDocumentImplementation,
            LspAction.Hover => LspMethods.TextDocumentHover,
            _ => throw new InvalidOperationException(
                $"Unsupported navigation action: {request.Action}."
            ),
        };

        var response = await session.RequestAsync(method, parameters, cancellationToken);
        if (response.IsError(out var requestError, out var result))
        {
            return requestError;
        }

        if (request.Action is LspAction.Hover)
        {
            return new HoverText(result.ToHoverText());
        }

        // A generated partial, such as a regex generator's, lists its type a second time
        return result.ToLocations(
            paths,
            request.Page.Limit,
            skipGenerated: request.Action is LspAction.Implementation,
            offset: request.Page.Offset
        ) switch
        {
            ToolError locationsError => locationsError,
            Locations locations when request.Context => SourceLines.Add(locations, paths),
            Locations locations => locations,
        };
    }

    private static bool IsEmpty(Result<ToolResult> result) =>
        result
            is Locations { Total: 0, Generated: 0 }
                or SymbolList { Total: 0 }
                or HoverText { Text: "" };

    private async Task<Result<ToolResult>> HierarchyAsync(
        NavigationRequest request,
        JsonObject position,
        CancellationToken cancellationToken
    )
    {
        var callersLookup =
            request.Action is LspAction.Callers
                ? await session.FindCallersAsync(position, cancellationToken)
                : ExtensionCallers.NotFound;
        if (callersLookup.IsError(out var callersError, out var callers))
        {
            return callersError;
        }

        var expansion = callers.Found
            ? new Hierarchy(callers.Calls, Accesses: 0, Prepared: true)
            : await ExpandHierarchyAsync(request.Action, position, cancellationToken);
        if (expansion.IsError(out var hierarchyError, out var hierarchy))
        {
            return hierarchyError;
        }

        if (
            hierarchy
                .Related.ToRelatedSymbols(paths, request.Page.Limit, request.Page.Offset)
                .IsError(out var symbolsError, out var symbols)
        )
        {
            return symbolsError;
        }

        var naming = await NameSymbolsAsync(
            symbols with
            {
                Accesses = hierarchy.Accesses,
                Hierarchy = hierarchy.Prepared ? request.Action : null,
            },
            cancellationToken
        );
        if (naming.IsError(out var namesError, out var result))
        {
            return namesError;
        }

        if (!request.Context)
        {
            return result;
        }

        // Outgoing call sites are in the requested document, not in the callee's file
        var callSiteFile =
            request.Action is LspAction.Callees
                ? paths.TryGetWorkspaceFile(position["textDocument"]!["uri"]!.GetValue<string>())
                : null;
        return SourceLines.Add(result, paths, callSiteFile);
    }

    private async Task<Result<Hierarchy>> ExpandHierarchyAsync(
        LspAction action,
        JsonObject position,
        CancellationToken cancellationToken
    )
    {
        var (prepare, expand) = action switch
        {
            LspAction.Callers => (
                LspMethods.TextDocumentPrepareCallHierarchy,
                LspMethods.CallHierarchyIncomingCalls
            ),
            LspAction.Callees => (
                LspMethods.TextDocumentPrepareCallHierarchy,
                LspMethods.CallHierarchyOutgoingCalls
            ),
            LspAction.Supertypes => (
                LspMethods.TextDocumentPrepareTypeHierarchy,
                LspMethods.TypeHierarchySupertypes
            ),
            _ => (LspMethods.TextDocumentPrepareTypeHierarchy, LspMethods.TypeHierarchySubtypes),
        };
        var related = new JsonArray();
        var accesses = 0;
        var preparation = await session.RequestAsync(prepare, position, cancellationToken);
        if (preparation.IsError(out var error, out var prepared))
        {
            return error;
        }

        var items = prepared as JsonArray ?? [];
        foreach (var item in items)
        {
            var expansion = await session.RequestAsync(
                expand,
                new JsonObject { ["item"] = item!.DeepClone() },
                cancellationToken
            );
            if (expansion.IsError(out var expandError, out var expanded))
            {
                return expandError;
            }

            foreach (var entry in expanded as JsonArray ?? [])
            {
                if (action is not LspAction.Callees)
                {
                    related.Add(entry!.DeepClone());
                    continue;
                }

                // Outgoing calls also list property, field and constant accesses, which hide the calls
                if (entry.Read<LspCall>().IsError(out var callError, out var call))
                {
                    return callError;
                }

                if (!LspSymbolKinds.IsInvocable(call.To?.Kind))
                {
                    accesses++;
                    continue;
                }

                related.Add(entry!.DeepClone());
            }
        }

        return new Hierarchy(related, accesses, Prepared: items.Count > 0);
    }

    private async Task<Result<SymbolList>> NameSymbolsAsync(
        SymbolList symbols,
        CancellationToken cancellationToken
    ) =>
        (await NameSymbolsAsync(symbols.Items, cancellationToken)).Map(items =>
            symbols with
            {
                Items = items,
            }
        );

    /// <summary>
    /// Items without a declaration in a workspace file keep Roslyn's name.
    /// </summary>
    private async Task<Result<List<SymbolItem>>> NameSymbolsAsync(
        IReadOnlyList<SymbolItem> items,
        CancellationToken cancellationToken
    )
    {
        var keys = items.Select(DeclarationKey).ToList();
        return (
            await resolver.NamesAsync(
                keys.Where(key => key.HasValue).Select(key => key!.Value),
                cancellationToken
            )
        ).Map(names =>
            items
                .Zip(keys, (item, key) =>
                    key is { } found && names.TryGetValue(found, out var name)
                        ? item with
                        {
                            Name = name,
                            Detail = null,
                        }
                        : item
                )
                .ToList()
        );
    }

    private static int MatchRank(string name, string query)
    {
        if (string.Equals(name, query, StringComparison.Ordinal))
        {
            return 0;
        }

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    private (string File, SourcePosition Position)? DeclarationKey(SymbolItem item) =>
        item is { File: { } file, Position: { } position } && paths.ResultFilePath(file) is { } path
            ? (path, position)
            : null;

    private async Task<Result<List<ScannedFile>>> ScanAsync(
        string[] batch,
        CancellationToken cancellationToken
    )
    {
        // Each open discards diagnostics of earlier solution versions, so the batch opens before any pull
        if (
            (await session.OpenDocumentsAsync(batch, cancellationToken)).IsError(
                out var openError,
                out var uris
            )
        )
        {
            return openError;
        }

        var projects = await session.WaitForProjectsAsync(batch, cancellationToken);
        if (projects.IsError(out var waitError, out var miscellaneous))
        {
            return waitError;
        }

        var reports = await Task.WhenAll(
            uris.Select(uri => session.DiagnosticReportAsync(uri, cancellationToken))
        );
        return reports
            .All()
            .Map(read =>
                batch
                    .Zip(read, (file, report) =>
                        new ScannedFile(file, report.Items ?? [], miscellaneous.Contains(file))
                    )
                    .ToList()
            );
    }

    private DiagnosticReport Report(List<ScannedFile> scanned, DiagnosticsRequest request)
    {
        var (limit, offset) = request.Page;
        var ids = request.Ids is { Count: > 0 }
            ? request.Ids.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        var found = new List<(string File, Diagnostic Diagnostic)>();
        var belowSeverity = 0;
        foreach (var file in scanned)
        {
            var matching = file
                .Reported.Where(diagnostic =>
                    ids is null || (diagnostic.CodeText() is { } code && ids.Contains(code))
                )
                .ToList();
            var selected = matching
                .Where(diagnostic => diagnostic.ToSeverity() <= request.Severity)
                .ToList();
            belowSeverity += matching.Count - selected.Count;
            if (selected.Count == 0)
            {
                continue;
            }

            var relative = paths.RelativePath(file.Path);
            found.AddRange(
                selected
                    .Select(diagnostic => diagnostic.ToDiagnostic())
                    .OrderBy(diagnostic => diagnostic.Start.Line)
                    .ThenBy(diagnostic => diagnostic.Start.Character)
                    .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
                    .Select(diagnostic => (relative, diagnostic))
            );
        }

        var codes = request.Summary ? Summarize(found) : [];
        var listed = request.Summary ? [] : found.Skip(offset).Take(limit);
        return new DiagnosticReport(
            scanned.Count,
            [
                .. listed
                    .GroupBy(entry => entry.File, StringComparer.Ordinal)
                    .Select(group => new FileDiagnostics(
                        group.Key,
                        [.. group.Select(entry => entry.Diagnostic)]
                    )),
            ],
            [.. codes.Skip(offset).Take(limit)],
            found.Select(entry => entry.File).Distinct(StringComparer.Ordinal).Count(),
            found.Count,
            belowSeverity,
            Paging.NextOffset(offset, limit, request.Summary ? codes.Count : found.Count),
            [
                .. scanned
                    .Where(file => file.Miscellaneous)
                    .Select(file => paths.RelativePath(file.Path)),
            ],
            codes.Count,
            ids is not null
        );
    }

    private static List<CodeSummary> Summarize(List<(string File, Diagnostic Diagnostic)> found) =>
        [
            .. found
                .GroupBy(entry => entry.Diagnostic.Code, StringComparer.Ordinal)
                .Select(group => new CodeSummary(
                    group.Count(),
                    group.First().File,
                    group.First().Diagnostic
                ))
                .OrderByDescending(summary => summary.Count)
                .ThenBy(summary => summary.Example.Code, StringComparer.Ordinal),
        ];

    private async Task<Result<JsonObject>> PositionParametersAsync(
        string file,
        SourcePosition position,
        CancellationToken cancellationToken
    )
    {
        if (
            (await session.OpenDocumentAsync(file, cancellationToken)).IsError(
                out var openError,
                out var uri
            )
        )
        {
            return openError;
        }

        if (paths.Resolve(file).IsError(out var pathError, out var path))
        {
            return pathError;
        }

        if (SourceLines.ReadLines(path).IsError(out var readError, out var lines))
        {
            return readError;
        }

        if (position.Line > lines.Length)
        {
            return ToolError.InvalidRequest(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Line {position.Line} is past the end of {paths.RelativePath(path)}, which has {lines.Length} lines."
                )
            );
        }

        var parameters = LspParameters.Document(uri);
        parameters["position"] = new JsonObject
        {
            ["line"] = position.Line - 1,
            ["character"] = position.Character - 1,
        };
        return parameters;
    }

    /// <summary>
    /// A hierarchy or impact request moves a usage position to its declaration.
    /// </summary>
    private async Task<Result<(string File, SourcePosition Position)>> LocateAsync(
        SymbolTarget target,
        LspAction action,
        CancellationToken cancellationToken
    )
    {
        if (target.Symbol is not null)
        {
            if (target is not { Line: null, Character: null })
            {
                return ToolError.InvalidRequest(
                    "Pass symbol with an optional file, or a file with a line and character."
                );
            }

            return (
                await resolver.ResolveAsync(
                    target.Symbol,
                    target.File,
                    action.TakesType,
                    cancellationToken
                )
            ).Map(found => (found[0].File, new SourcePosition(found[0].Line, found[0].Character)));
        }

        if (target is not { File: not null, Line: > 0, Character: > 0 })
        {
            return ToolError.InvalidRequest(
                "Provide symbol, or a file with a one-based line and character."
            );
        }

        var position = new SourcePosition(target.Line.Value, target.Character.Value);
        return action.IsHierarchy || action is LspAction.Impact
            ? await DeclarationOfAsync(target.File, position, cancellationToken)
            : (target.File, position);
    }

    /// <summary>
    /// Roslyn prepares no call hierarchy at a member access such as <c>value.Method()</c>.
    /// Returns the position itself without a single workspace declaration.
    /// </summary>
    private async Task<Result<(string File, SourcePosition Position)>> DeclarationOfAsync(
        string file,
        SourcePosition position,
        CancellationToken cancellationToken
    )
    {
        var positioned = await PositionParametersAsync(file, position, cancellationToken);
        if (positioned.IsError(out var error, out var parameters))
        {
            return error;
        }

        var response = await session.RequestAsync(
            LspMethods.TextDocumentDefinition,
            parameters,
            cancellationToken
        );
        if (response.IsError(out var requestError, out var definition))
        {
            return requestError;
        }

        if (
            definition
                .FindLocations(paths, skipGenerated: false)
                .IsError(out var invalid, out var locations)
        )
        {
            return invalid;
        }

        if (
            locations.Positions is [var (found, declaration)]
            && paths.ResultFilePath(found) is { } declared
        )
        {
            return (declared, declaration);
        }

        return (file, position);
    }

    private Result<List<string>> SelectFiles(string? file, CancellationToken cancellationToken)
    {
        var patterns = file is null or "*" ? ["**/*.cs"] : ExpandBraces(file);
        if (patterns.TrueForAll(pattern => pattern.IndexOfAny(['*', '?']) < 0))
        {
            return patterns
                .Select(paths.Resolve)
                .All()
                .Map(resolved => resolved.Distinct(StringComparer.Ordinal).ToList());
        }

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddIncludePatterns(patterns);
        return paths
            .EnumerateFiles(cancellationToken)
            .Map(files =>
                matcher
                    .Match(paths.Root, files.Select(static file => file.Path))
                    .Files.Select(match => Path.GetFullPath(match.Path, paths.Root))
                    .Where(WorkspacePaths.IsCSharpFile)
                    .Order(StringComparer.Ordinal)
                    .ToList()
            );
    }

    // The matcher has no brace alternatives, so `src/{A,B}.cs` becomes one pattern per alternative
    private static List<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{', StringComparison.Ordinal);
        if (open < 0)
        {
            return [pattern];
        }

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

    private sealed record ScannedFile(string Path, LspDiagnostic[] Reported, bool Miscellaneous);

    /// <param name="Prepared">False when Roslyn found no symbol to expand.</param>
    private readonly record struct Hierarchy(JsonArray Related, int Accesses, bool Prepared);
}
