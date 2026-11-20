// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynConnection
{
    internal static readonly TimeSpan ProjectAttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _projectAttachPollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Returns no contexts when the server cannot answer or cancels the request.
    /// </summary>
    public async Task<Result<ProjectContext[]>> ProjectContextsAsync(
        string uri,
        CancellationToken cancellationToken
    ) =>
        await RequestProjectContextsAsync(uri, cancellationToken) switch
        {
            ToolError { RpcCode: not null } => Array.Empty<ProjectContext>(),
            var contexts => contexts,
        };

    /// <summary>
    /// Returns the files outside every project. Waits for Roslyn to attach files created since the load.
    /// </summary>
    public async Task<Result<HashSet<string>>> WaitForProjectsAsync(
        IReadOnlyCollection<string> files,
        CancellationToken cancellationToken
    )
    {
        var opened = await OpenDocumentsAsync(files, cancellationToken);
        if (opened.IsError(out var openError, out var uris))
            return openError;

        var pending = files.Zip(uris).ToList();
        var outside = new HashSet<string>(StringComparer.Ordinal);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            var states = await Task.WhenAll(
                pending.Select(file => MembershipAsync(file.Second, cancellationToken))
            );
            if (states.All().IsError(out var error, out var memberships))
                return error;

            List<(string First, string Second)> waiting = [];
            foreach (var (file, membership) in pending.Zip(memberships))
            {
                if (membership is Membership.InProject)
                    continue;

                // A file that existed at load and is still outside a project stays there
                if (membership is Membership.Unknown || _createdFiles.ContainsKey(file.First))
                    waiting.Add(file);
                else
                    outside.Add(file.First);
            }

            pending = waiting;

            if (pending.Count == 0 || Stopwatch.GetElapsedTime(started) >= ProjectAttachTimeout)
            {
                // A created file that no project picks up is waited for only once
                foreach (var (path, _) in pending)
                {
                    if (_createdFiles.TryRemove(path, out _))
                        outside.Add(path);
                }

                return outside;
            }

            await Task.Delay(_projectAttachPollInterval, cancellationToken);
        }
    }

    // Roslyn cancels the request during a workspace change, so the next poll asks again
    private async Task<Result<Membership>> MembershipAsync(
        string uri,
        CancellationToken cancellationToken
    ) =>
        await RequestProjectContextsAsync(uri, cancellationToken) switch
        {
            ToolError error when IsCancelled(error) => Membership.Unknown,
            ToolError { RpcCode: not null } => Membership.InProject,
            ToolError error => error,
            ProjectContext[] contexts
                when contexts.Length > 0 && contexts.All(context => context.IsMiscellaneous) =>
                Membership.Outside,
            ProjectContext[] => Membership.InProject,
        };

    private async Task<Result<ProjectContext[]>> RequestProjectContextsAsync(
        string uri,
        CancellationToken cancellationToken
    )
    {
        var response = await RequestAsync(
            LspMethods.TextDocumentGetProjectContexts,
            new JsonObject { ["_vs_textDocument"] = new JsonObject { ["uri"] = uri } },
            cancellationToken
        );
        return response.Bind(result =>
            result is null
                ? Array.Empty<ProjectContext>()
                : result.Read<ProjectContexts>().Map(found => found.Contexts ?? [])
        );
    }

    private enum Membership
    {
        InProject = 0,
        Outside = 1,
        Unknown = 2,
    }
}
