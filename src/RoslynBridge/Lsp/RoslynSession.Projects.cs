// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Text.Json.Nodes;
using StreamJsonRpc;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    internal static readonly TimeSpan ProjectAttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _projectAttachPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Returns the project contexts of an open document, or none when the server lacks the extension.
    /// </summary>
    public async Task<JsonArray> ProjectContextsAsync(
        string uri,
        CancellationToken cancellationToken
    )
    {
        JsonNode? result;
        try
        {
            result = await RequestAsync(
                LspMethods.TextDocumentGetProjectContexts,
                new JsonObject { ["_vs_textDocument"] = new JsonObject { ["uri"] = uri } },
                cancellationToken
            );
        }
        catch (RemoteInvocationException)
        {
            return [];
        }

        return result?["_vs_projectContexts"] as JsonArray ?? [];
    }

    /// <summary>
    /// Opens the C# files and returns those outside every project, after waiting for Roslyn
    /// to attach the files created since the load. Roslyn treats a new file as a miscellaneous
    /// file, without semantic diagnostics, until its project picks the file up.
    /// </summary>
    public async Task<HashSet<string>> WaitForProjectsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    )
    {
        var uris = await OpenDocumentsAsync(files, cancellationToken);
        var pending = files.Zip(uris).ToList();
        var outside = new HashSet<string>(StringComparer.Ordinal);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var states = await Task.WhenAll(
                pending.Select(async file =>
                    (
                        File: file,
                        Miscellaneous: await IsMiscellaneousAsync(file.Second, cancellationToken)
                    )
                )
            );
            pending = [];
            foreach (var (file, _) in states.Where(state => state.Miscellaneous))
            {
                // A file that existed at load and is still outside a project stays there
                if (_createdFiles.ContainsKey(file.First))
                {
                    pending.Add(file);
                }
                else
                {
                    outside.Add(file.First);
                }
            }

            if (pending.Count == 0 || Stopwatch.GetElapsedTime(started) >= ProjectAttachTimeout)
            {
                // A created file that no project picks up is waited for only once
                foreach (var (path, _) in pending)
                {
                    _createdFiles.TryRemove(path, out _);
                    outside.Add(path);
                }

                return outside;
            }

            await Task.Delay(_projectAttachPollInterval, cancellationToken);
        }
    }

    private async Task<bool> IsMiscellaneousAsync(string uri, CancellationToken cancellationToken)
    {
        // Servers without the extension cannot report the state, so the file is not held back
        var contexts = await ProjectContextsAsync(uri, cancellationToken);
        return contexts.Count > 0
            && contexts.All(context => context?["_vs_is_miscellaneous"]?.GetValue<bool>() is true);
    }
}
