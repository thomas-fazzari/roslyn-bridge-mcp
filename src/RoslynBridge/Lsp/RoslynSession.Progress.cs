// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Immutable;

namespace RoslynBridge.Lsp;

internal sealed partial class RoslynSession
{
    private const string ProgressBegin = "begin";
    private const string ProgressReport = "report";
    private const string ProgressEnd = "end";

    // Replaced as a whole, so readers never wait for a notification
    private ImmutableList<TrackedProgress> _progress = [];

    /// <summary>
    /// The latest progress Roslyn began and has not ended, or null when the workspace is not loading or Roslyn reported none.
    /// </summary>
    public LoadProgress? Progress =>
        IsLoading ? Volatile.Read(ref _progress).LastOrDefault()?.Progress : null;

    /// <summary>
    /// Applies a <c>$/progress</c> value to the progress that carries the token.
    /// </summary>
    internal void ReportProgress(string token, WorkDoneProgress value) =>
        ImmutableInterlocked.Update(ref _progress, tracked => WithProgress(tracked, token, value));

    private void ClearProgress() => Volatile.Write(ref _progress, []);

    /// <returns>The tracked progress after the value, which a report for an unknown token leaves unchanged.</returns>
    private static ImmutableList<TrackedProgress> WithProgress(
        ImmutableList<TrackedProgress> tracked,
        string token,
        WorkDoneProgress value
    )
    {
        var index = tracked.FindIndex(entry =>
            string.Equals(entry.Token, token, StringComparison.Ordinal)
        );
        var percentage = value.Percentage;
        switch (value.Kind)
        {
            case ProgressBegin:
                var begun = new TrackedProgress(token, new LoadProgress(Title(value), percentage));
                return (index < 0 ? tracked : tracked.RemoveAt(index)).Add(begun);

            case ProgressReport when index >= 0:
                var current = tracked[index];
                return tracked.SetItem(
                    index,
                    current with
                    {
                        Progress = current.Progress with
                        {
                            Percentage = percentage ?? current.Progress.Percentage,
                        },
                    }
                );

            case ProgressEnd when index >= 0:
                return tracked.RemoveAt(index);

            default:
                return tracked;
        }
    }

    /// <returns>The title without the trailing dots that Roslyn adds, as in Loading Workspace.slnx...</returns>
    private static string Title(WorkDoneProgress value) =>
        value.Title?.TrimEnd('.', '\u2026') ?? string.Empty;

    private sealed record TrackedProgress(string Token, LoadProgress Progress);
}
