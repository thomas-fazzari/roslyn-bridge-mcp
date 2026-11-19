// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    internal static readonly TimeSpan ProjectAttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _projectAttachPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Returns no contexts when the server lacks the extension.
    /// </summary>
    public async Task<Result<ProjectContext[]>> ProjectContextsAsync(
        string uri,
        CancellationToken cancellationToken
    )
    {
        var response = await RequestAsync(
            LspMethods.TextDocumentGetProjectContexts,
            new JsonObject { ["_vs_textDocument"] = new JsonObject { ["uri"] = uri } },
            cancellationToken
        );
        if (response.IsError(out var error, out var result))
        {
            return error is { RpcCode: not null } ? Array.Empty<ProjectContext>() : error;
        }

        if (result is null)
        {
            return Array.Empty<ProjectContext>();
        }

        return result.Read<ProjectContexts>().Map(found => found.Contexts ?? []);
    }

    /// <summary>
    /// Returns the files outside every project. Waits for Roslyn to attach files created since the load.
    /// </summary>
    public async Task<Result<HashSet<string>>> WaitForProjectsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    )
    {
        if (
            (await OpenDocumentsAsync(files, cancellationToken)).IsError(
                out var openError,
                out var uris
            )
        )
        {
            return openError;
        }

        var pending = files.Zip(uris).ToList();
        var outside = new HashSet<string>(StringComparer.Ordinal);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var states = await Task.WhenAll(
                pending.Select(file => IsMiscellaneousAsync(file.Second, cancellationToken))
            );
            if (states.All().IsError(out var error, out var flags))
            {
                return error;
            }

            var stillOutside = pending.Where((_, index) => flags[index]).ToList();
            pending = [];
            foreach (var file in stillOutside)
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

    // Servers without the extension cannot report the state, so the file is not held back
    private async Task<Result<bool>> IsMiscellaneousAsync(
        string uri,
        CancellationToken cancellationToken
    ) =>
        (await ProjectContextsAsync(uri, cancellationToken)).Map(contexts =>
            contexts.Length > 0 && contexts.All(context => context.IsMiscellaneous)
        );
}
