// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.FileSystemGlobbing;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
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

    public async Task<Result<JsonNode?>> NavigationAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var position = await PositionAsync(request, cancellationToken);
        if (position.Error is { } error)
        {
            return error;
        }

        var parameters = position.Value;
        if (
            request.Action
            is LspAction.Callers
                or LspAction.Callees
                or LspAction.Supertypes
                or LspAction.Subtypes
        )
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
            parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        }

        var result = await session.RequestAsync(method, parameters, cancellationToken);
        if (request.Action is LspAction.Hover)
        {
            return result.ToCompactHover();
        }

        var locations = result.ToCompactLocations(
            paths,
            request.Limit,
            skipGenerated: request.Action is LspAction.References
        );
        if (request.Context)
        {
            SourceLines.Add(locations, paths, callSiteFile: null);
        }

        return locations;
    }

    private async Task<JsonObject> HierarchyAsync(
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

        var result = related.ToCompactHierarchy(paths, request.Limit);
        if (accesses > 0)
        {
            result["accesses"] = accesses;
        }

        await NameSymbolsAsync(result, cancellationToken);
        if (request.Context)
        {
            // Outgoing call sites are in the requested document, not in the callee's file
            var callSiteFile =
                request.Action is LspAction.Callees
                    ? paths.TryGetWorkspaceFile(
                        position["textDocument"]!["uri"]!.GetValue<string>()
                    )
                    : null;
            SourceLines.Add(result, paths, callSiteFile);
        }

        return result;
    }

    /// <summary>
    /// Replaces the name and container Roslyn reports with a symbol name that navigate and edit accept.
    /// Items without a declaration in a workspace file keep Roslyn's name.
    /// </summary>
    private async Task NameSymbolsAsync(JsonObject result, CancellationToken cancellationToken)
    {
        var positions = new List<(JsonObject Item, (string File, int Line, int Character) Key)>();
        foreach (var item in (result["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (
                item["file"]?.GetValue<string>() is { } file
                && paths.ResultFilePath(file) is { } path
                && item["position"] is JsonArray position
            )
            {
                positions.Add(
                    (item, (path, position[0]!.GetValue<int>(), position[1]!.GetValue<int>()))
                );
            }
        }

        var names = await resolver.NamesAsync(
            positions.Select(position => position.Key),
            cancellationToken
        );
        foreach (var (item, key) in positions)
        {
            if (names.TryGetValue(key, out var name))
            {
                item.Remove("name");
                item.Remove("detail");
                item["symbol"] = name;
            }
        }
    }

    public async Task<JsonNode?> SymbolsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.File is null)
        {
            var symbols = await session.RequestAsync(
                LspMethods.WorkspaceSymbol,
                new JsonObject { ["query"] = request.Query ?? string.Empty },
                cancellationToken
            );
            return symbols.ToCompactSymbols(paths, request.Limit);
        }

        var uri = await session.OpenDocumentAsync(request.File, cancellationToken);
        var documentSymbols = await session.RequestAsync(
            LspMethods.TextDocumentDocumentSymbol,
            Document(uri),
            cancellationToken
        );
        return documentSymbols.ToCompactSymbols(paths, request.Limit);
    }

    public async Task<Result<JsonNode?>> DiagnosticsAsync(
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

        var results = new JsonArray();
        var remaining = request.Limit;
        var total = 0;
        var belowSeverity = 0;
        var filesWithDiagnostics = 0;
        var miscellaneousFiles = new JsonArray();
        foreach (var batch in files.Chunk(RoslynSession.MaximumOpenDocuments))
        {
            // Each open discards diagnostics of earlier solution versions, so the batch opens before any pull
            var uris = new List<string>(batch.Length);
            foreach (var file in batch)
            {
                uris.Add(await session.OpenDocumentAsync(file, cancellationToken));
            }

            var miscellaneous = await WaitForProjectsAsync(uris, cancellationToken);
            // Diagnostic pulls are read-only, so the batch runs concurrently
            var reports = await Task.WhenAll(
                uris.Select(uri => FileDiagnosticsAsync(uri, cancellationToken))
            );

            for (var index = 0; index < batch.Length; index++)
            {
                var file = batch[index];
                if (miscellaneous.Contains(uris[index]))
                {
                    miscellaneousFiles.Add((JsonNode)paths.RelativePath(file));
                }

                var reported = reports[index]["items"] as JsonArray ?? [];
                var diagnostics = reported
                    .Where(diagnostic => IsReported(diagnostic!, request.Severity))
                    .ToList();
                belowSeverity += reported.Count - diagnostics.Count;
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

                var selected = new JsonArray();

                foreach (var diagnostic in diagnostics.Take(remaining))
                {
                    selected.Add((JsonNode)diagnostic!.ToCompactDiagnostic());
                }

                remaining -= selected.Count;
                results.Add(
                    (JsonNode)
                        new JsonObject
                        {
                            ["file"] = paths.RelativePath(file),
                            ["diagnostics"] = selected,
                        }
                );
            }
        }

        var result = new JsonObject
        {
            ["filesChecked"] = files.Length,
            ["filesWithDiagnostics"] = filesWithDiagnostics,
            ["diagnostics"] = results,
            ["total"] = total,
            ["belowSeverity"] = belowSeverity,
            ["truncated"] = total > request.Limit,
        };

        if (miscellaneousFiles.Count > 0)
        {
            result["miscellaneousFiles"] = miscellaneousFiles;
        }

        return result;
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

    private static bool IsReported(JsonNode diagnostic, DiagnosticSeverity minimum) =>
        diagnostic.Severity() <= minimum;

    public async Task<JsonObject> FileDiagnosticsAsync(
        string uri,
        CancellationToken cancellationToken
    )
    {
        var response = await session.RequestAsync(
            LspMethods.TextDocumentDiagnostic,
            Document(uri),
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

        var (file, line, character) = location.Value;
        var uri = await session.OpenDocumentAsync(file, cancellationToken);
        return new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = line - 1, ["character"] = character - 1 },
        };
    }

    /// <summary>
    /// Returns the one-based position a request targets, from its symbol name or its explicit position.
    /// </summary>
    private async Task<Result<(string File, int Line, int Character)>> LocateAsync(
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
            return (declaration.File, declaration.Line, declaration.Character);
        }

        if (request is not { File: not null, Line: > 0, Character: > 0 })
        {
            return ToolError.InvalidRequest(
                "Provide symbol, or a file with a one-based line and character."
            );
        }

        return (request.File, request.Line.Value, request.Character.Value);
    }

    public static JsonObject Document(string uri) =>
        new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };

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
            .Order(StringComparer.Ordinal);
    }
}
