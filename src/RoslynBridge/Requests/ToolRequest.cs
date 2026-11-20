// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Requests;

internal closed record ToolRequest
{
    public abstract CallPolicy Policy { get; }

    public virtual Paging Page => Paging.Default;

    /// <summary>
    /// Passes every file name of the call through <paramref name="resolve"/>, which the bridge
    /// uses both to collect the names and to replace them with workspace paths.
    /// </summary>
    public virtual ToolRequest WithFiles(FileResolver resolve) => this;

    public Result Validate() =>
        Page switch
        {
            { Limit: < 1 or > Paging.MaximumLimit } => ToolError.InvalidRequest(
                $"Limit must be between 1 and {Paging.MaximumLimit}."
            ),
            { Offset: < 0 } => ToolError.InvalidRequest("Offset must not be negative."),
            _ => Result.Success,
        };

    /// <summary>
    /// Receives the files that match several workspace files, in the order of their names.
    /// </summary>
    public virtual Result<ToolRequest> WithFileErrors(
        IReadOnlyList<(string Name, ToolError Error)> errors
    ) => errors is [(_, var error), ..] ? error : this;
}

/// <summary>
/// Maps a file name of the call to its workspace path, or returns it unchanged.
/// </summary>
[return: NotNullIfNotNull(nameof(file))]
internal delegate string? FileResolver(string? file);

/// <param name="Action">Names the call in logs and in proposals.</param>
/// <param name="MayRestart">Whether the call may restart Roslyn, so it gets the startup timeout as well.</param>
/// <param name="Exclusive">Whether the call waits for the gate, runs under the timeout and resolves its files.</param>
internal readonly record struct CallPolicy(
    LspAction Action,
    ServerPreparation Server = ServerPreparation.StartAndSynchronize,
    bool MayRestart = false,
    FileScope Files = FileScope.CSharp,
    bool Exclusive = true
);

/// <summary>
/// What the bridge does with Roslyn before the call runs.
/// </summary>
internal enum ServerPreparation
{
    StartAndSynchronize = 0,

    // The call synchronizes Roslyn itself, so an earlier synchronization repeats work
    Start = 1,

    None = 2,
}

internal enum FileScope
{
    CSharp = 0,
    CSharpOrGlob = 1,
    AnyFile = 2,
}

internal sealed record Paging(int Limit, int Offset)
{
    internal const int DefaultLimit = 50;
    internal const int MaximumLimit = 250;
    internal const string LimitDescription =
        "Maximum returned results, from 1 to 250. Truncation is explicit.";
    internal const string OffsetDescription = "Results to skip, for paging.";

    public static Paging Default { get; } = new(DefaultLimit, 0);

    public IEnumerable<T> Slice<T>(IEnumerable<T> items) => items.Skip(Offset).Take(Limit);

    public int? NextOffset(int total) => Offset < total - Limit ? Offset + Limit : null;
}
