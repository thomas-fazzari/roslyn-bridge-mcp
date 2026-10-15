// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
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
    private static readonly TimeSpan _projectAttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _projectAttachPollInterval = TimeSpan.FromMilliseconds(200);

    public async Task<Result<ToolResult>> NavigationAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
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

        var position = await PositionAsync(request, cancellationToken);
        if (position.Error is { } error)
        {
            return error;
        }

        var parameters = position.Value;
        if (request.Action.IsHierarchy)
        {
            return await HierarchyAsync(request, parameters, cancellationToken);
        }

        var method = request.Action switch
        {
            LspAction.Definition => LspMethods.TextDocumentDefinition,
            LspAction.TypeDefinition => LspMethods.TextDocumentTypeDefinition,
            LspAction.Implementation => LspMethods.TextDocumentImplementation,
            LspAction.References => LspMethods.TextDocumentReferences,
            LspAction.Hover => LspMethods.TextDocumentHover,
            _ => throw new InvalidOperationException(
                $"Unsupported navigation action: {request.Action}."
            ),
        };

        if (request.Action is LspAction.References)
        {
            // definition already gives the declaration, which would read as one more usage
            parameters["context"] = new JsonObject { ["includeDeclaration"] = false };
        }

        var result = await session.RequestAsync(method, parameters, cancellationToken);
        if (request.Action is LspAction.Hover)
        {
            return new HoverText(result.ToHoverText());
        }

        var locations = result.ToLocations(
            paths,
            request.Limit,
            skipGenerated: request.Action is LspAction.References
        );
        return request.Context ? SourceLines.Add(locations, paths) : locations;
    }

    private async Task<SymbolList> HierarchyAsync(
        LspRequest request,
        JsonObject position,
        CancellationToken cancellationToken
    )
    {
        var (prepare, expand) = request.Action switch
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
        var items = await session.RequestAsync(prepare, position, cancellationToken) as JsonArray;

        foreach (var item in items ?? [])
        {
            var expanded = await session.RequestAsync(
                expand,
                new JsonObject { ["item"] = item!.DeepClone() },
                cancellationToken
            );
            foreach (var entry in expanded as JsonArray ?? [])
            {
                // Outgoing calls also list property, field and constant accesses, which hide the calls
                if (
                    request.Action is LspAction.Callees
                    && !LspSymbolKinds.IsInvocable(entry!["to"]?["kind"]?.GetValue<int>())
                )
                {
                    accesses++;
                    continue;
                }

                related.Add(entry!.DeepClone());
            }
        }

        var result = await NameSymbolsAsync(
            related.ToRelatedSymbols(paths, request.Limit) with
            {
                Accesses = accesses,
            },
            cancellationToken
        );
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

    /// <summary>
    /// Replaces the name and container Roslyn reports with a symbol name that navigate and edit accept.
    /// Items without a declaration in a workspace file keep Roslyn's name.
    /// </summary>
    private async Task<SymbolList> NameSymbolsAsync(
        SymbolList symbols,
        CancellationToken cancellationToken
    )
    {
        var keys = symbols.Items.Select(DeclarationKey).ToList();
        var names = await resolver.NamesAsync(
            keys.Where(key => key.HasValue).Select(key => key!.Value),
            cancellationToken
        );
        return symbols with
        {
            Items =
            [
                .. symbols.Items.Zip(
                    keys,
                    (item, key) =>
                        key is { } found && names.TryGetValue(found, out var name)
                            ? item with
                            {
                                Name = name,
                                Detail = null,
                            }
                            : item
                ),
            ],
        };
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

    public async Task<SymbolList> SymbolsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.File is null)
        {
            var query = request.Query ?? string.Empty;
            var symbols = await session.RequestAsync(
                LspMethods.WorkspaceSymbol,
                new JsonObject { ["query"] = query },
                cancellationToken
            );
            // Roslyn returns fuzzy matches in file order, so exact and prefix matches come first
            JsonNode ranked = new JsonArray([
                .. (symbols as JsonArray ?? [])
                    .OrderBy(symbol =>
                        MatchRank(symbol?["name"]?.GetValue<string>() ?? string.Empty, query)
                    )
                    .Select(symbol => symbol?.DeepClone()),
            ]);
            return ranked.ToSymbolList(paths, request.Limit);
        }

        var uri = await session.OpenDocumentAsync(request.File, cancellationToken);
        var documentSymbols = await session.RequestAsync(
            LspMethods.TextDocumentDocumentSymbol,
            LspParameters.Document(uri),
            cancellationToken
        );
        return documentSymbols.ToSymbolList(paths, request.Limit);
    }

    public async Task<Result<DiagnosticReport>> DiagnosticsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var files = SelectFiles(request.File).Take(MaximumDiagnosticFiles + 1).ToArray();
        if (files.Length > MaximumDiagnosticFiles)
        {
            return ToolError.InvalidRequest(
                $"Diagnostics accepts at most {MaximumDiagnosticFiles} files. Use a narrower glob."
            );
        }

        await session.SynchronizeNewFilesAsync(files, cancellationToken);
        var scanned = new List<ScannedFile>(files.Length);
        foreach (var batch in files.Chunk(RoslynSession.MaximumOpenDocuments))
        {
            scanned.AddRange(await ScanAsync(batch, cancellationToken));
        }

        return Report(scanned, request);
    }

    private async Task<IEnumerable<ScannedFile>> ScanAsync(
        string[] batch,
        CancellationToken cancellationToken
    )
    {
        // Each open discards diagnostics of earlier solution versions, so the batch opens before any pull
        var uris = await session.OpenDocumentsAsync(batch, cancellationToken);

        var miscellaneous = await WaitForProjectsAsync(uris, cancellationToken);
        var reports = await Task.WhenAll(
            uris.Select(uri => FileDiagnosticsAsync(uri, cancellationToken))
        );
        return batch.Select(
            (file, index) =>
                new ScannedFile(
                    file,
                    reports[index]["items"] as JsonArray ?? [],
                    miscellaneous.Contains(uris[index])
                )
        );
    }

    private DiagnosticReport Report(List<ScannedFile> scanned, LspRequest request)
    {
        var files = new List<FileDiagnostics>();
        var remaining = request.Limit;
        var total = 0;
        var belowSeverity = 0;
        var filesWithDiagnostics = 0;
        foreach (var file in scanned)
        {
            var diagnostics = file
                .Reported.Where(diagnostic => diagnostic!.Severity() <= request.Severity)
                .ToList();
            belowSeverity += file.Reported.Count - diagnostics.Count;
            total += diagnostics.Count;
            if (diagnostics.Count == 0)
            {
                continue;
            }

            filesWithDiagnostics++;
            if (remaining == 0)
            {
                continue;
            }

            var selected = diagnostics
                .Select(diagnostic => diagnostic!.ToDiagnostic())
                .OrderBy(diagnostic => diagnostic.Start.Line)
                .ThenBy(diagnostic => diagnostic.Start.Character)
                .Take(remaining)
                .ToList();
            remaining -= selected.Count;
            files.Add(new FileDiagnostics(paths.RelativePath(file.Path), selected));
        }

        return new DiagnosticReport(
            scanned.Count,
            files,
            filesWithDiagnostics,
            total,
            belowSeverity,
            total > request.Limit,
            [
                .. scanned
                    .Where(file => file.Miscellaneous)
                    .Select(file => paths.RelativePath(file.Path)),
            ]
        );
    }

    /// <summary>
    /// Waits until Roslyn attaches the documents to a project and returns those still outside one.
    /// Roslyn treats a new file as a miscellaneous file, without semantic diagnostics,
    /// until its project picks the file up.
    /// </summary>
    private async Task<HashSet<string>> WaitForProjectsAsync(
        IReadOnlyList<string> uris,
        CancellationToken cancellationToken
    )
    {
        var pending = new HashSet<string>(uris, StringComparer.Ordinal);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var states = await Task.WhenAll(
                pending.Select(async uri =>
                    (Uri: uri, Miscellaneous: await IsMiscellaneousAsync(uri, cancellationToken))
                )
            );
            pending = states
                .Where(state => state.Miscellaneous)
                .Select(state => state.Uri)
                .ToHashSet(StringComparer.Ordinal);

            if (pending.Count == 0 || Stopwatch.GetElapsedTime(started) >= _projectAttachTimeout)
            {
                return pending;
            }

            await Task.Delay(_projectAttachPollInterval, cancellationToken);
        }
    }

    private async Task<bool> IsMiscellaneousAsync(string uri, CancellationToken cancellationToken)
    {
        // Servers without the extension cannot report the state, so the file is not held back
        var contexts = await session.ProjectContextsAsync(uri, cancellationToken);
        return contexts.Count > 0
            && contexts.All(context => context?["_vs_is_miscellaneous"]?.GetValue<bool>() is true);
    }

    public async Task<JsonObject> FileDiagnosticsAsync(
        string uri,
        CancellationToken cancellationToken
    )
    {
        var response = await session.RequestAsync(
            LspMethods.TextDocumentDiagnostic,
            LspParameters.Document(uri),
            cancellationToken
        );
        return response as JsonObject
            ?? throw new ToolErrorException(
                ToolError.InvalidLspData("Roslyn did not return a diagnostic report.")
            );
    }

    public async Task<Result<JsonObject>> PositionAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var location = await LocateAsync(request, cancellationToken);
        if (location.Error is { } error)
        {
            return error;
        }

        var (file, position) = location.Value;
        return await PositionParametersAsync(file, position, cancellationToken);
    }

    private async Task<JsonObject> PositionParametersAsync(
        string file,
        SourcePosition position,
        CancellationToken cancellationToken
    )
    {
        var parameters = LspParameters.Document(
            await session.OpenDocumentAsync(file, cancellationToken)
        );
        var lineCount = SourceLines.ReadLines(paths.Resolve(file)).Length;
        if (position.Line > lineCount)
        {
            throw new ToolErrorException(
                ToolError.InvalidRequest(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Line {position.Line} is past the end of {paths.RelativePath(paths.Resolve(file))}, which has {lineCount} lines."
                    )
                )
            );
        }

        parameters["position"] = new JsonObject
        {
            ["line"] = position.Line - 1,
            ["character"] = position.Character - 1,
        };
        return parameters;
    }

    /// <summary>
    /// Returns the one-based position a request targets, from its symbol name or its explicit position.
    /// </summary>
    private async Task<Result<(string File, SourcePosition Position)>> LocateAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Symbol is not null)
        {
            if (request is not { Line: null, Character: null, EndLine: null, EndCharacter: null })
            {
                return ToolError.InvalidRequest(
                    "Pass symbol with an optional file, or a file with a line and character."
                );
            }

            var resolved = await resolver.ResolveAsync(
                request.Symbol,
                request.File,
                cancellationToken
            );
            if (resolved.Error is { } error)
            {
                return error;
            }

            var declaration = resolved.Value[0];
            return (declaration.File, new SourcePosition(declaration.Line, declaration.Character));
        }

        if (request is not { File: not null, Line: > 0, Character: > 0 })
        {
            return ToolError.InvalidRequest(
                "Provide symbol, or a file with a one-based line and character."
            );
        }

        return (request.File, new SourcePosition(request.Line.Value, request.Character.Value));
    }

    private IEnumerable<string> SelectFiles(string? file)
    {
        if (
            file?.Contains('*', StringComparison.Ordinal) is false
            && !file.Contains('?', StringComparison.Ordinal)
        )
        {
            return [paths.Resolve(file)];
        }

        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(file is null or "*" ? "**/*.cs" : file);
        return matcher
            .Match(paths.Root, paths.EnumerateFiles().Select(static file => file.Path))
            .Files.Select(match => Path.GetFullPath(match.Path, paths.Root))
            .Where(RoslynSession.IsCSharpFile)
            .Order(StringComparer.Ordinal);
    }

    private sealed record ScannedFile(string Path, JsonArray Reported, bool Miscellaneous);
}
