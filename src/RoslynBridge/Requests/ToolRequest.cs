// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Requests;

internal closed record ToolRequest
{
    public abstract CallPolicy Policy { get; }

    public virtual Paging Page => Paging.Default;

    /// <summary>
    /// The file names that the bridge resolves to workspace paths before the call.
    /// </summary>
    public virtual IEnumerable<string?> FileNames => [];

    public virtual ToolRequest WithFiles(Func<string?, string?> resolve) => this;
}

/// <param name="Action">Names the call in logs and in proposals.</param>
/// <param name="StartsServer">Whether the call starts Roslyn and synchronizes it before it runs.</param>
/// <param name="MayRestart">Whether the call may restart Roslyn, so it gets the startup timeout as well.</param>
/// <param name="SynchronizesItself">Whether the call synchronizes Roslyn itself, so an earlier synchronization repeats work.</param>
internal readonly record struct CallPolicy(
    LspAction Action,
    bool StartsServer = true,
    bool MayRestart = false,
    bool SynchronizesItself = false,
    FileScope Files = FileScope.CSharp
);

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

    public static Paging Default { get; } = new(DefaultLimit, 0);

    internal static int? NextOffset(int offset, int limit, int total) =>
        offset < total - limit ? offset + limit : null;
}

/// <summary>
/// A symbol name with an optional file, or a file with a one-based position.
/// </summary>
internal sealed record SymbolTarget(string? Symbol, string? File, int? Line, int? Character)
{
    public static SymbolTarget At(string file, SourcePosition position) =>
        new(Symbol: null, file, position.Line, position.Character);

    public SymbolTarget WithFile(Func<string?, string?> resolve) =>
        this with
        {
            File = resolve(File),
        };
}

internal sealed record NavigationRequest(
    LspAction Action,
    SymbolTarget Target,
    bool Inherited,
    bool Context,
    Paging Page
) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy => new(Action);

    public override IEnumerable<string?> FileNames => [Target.File];

    public override NavigationRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            Target = Target.WithFile(resolve),
        };
}

/// <param name="Queries">Each query carries the call's context and paging. Page repeats the paging for validation.</param>
internal sealed record BulkRequest(IReadOnlyList<NavigationRequest> Queries, Paging Page)
    : ToolRequest
{
    internal const int MaximumQueries = 16;

    public override Paging Page { get; } = Page;

    public override CallPolicy Policy => new(LspAction.Bulk);

    public override IEnumerable<string?> FileNames => Queries.SelectMany(query => query.FileNames);

    public override BulkRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            Queries = [.. Queries.Select(query => query.WithFiles(resolve))],
        };
}

internal sealed record DiagnosticsRequest(
    string? File,
    DiagnosticSeverity Severity,
    IReadOnlyList<string>? Ids,
    bool Summary,
    Paging Page
) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy =>
        new(LspAction.Diagnostics, SynchronizesItself: true, Files: FileScope.CSharpOrGlob);

    public override IEnumerable<string?> FileNames => [File];

    public override DiagnosticsRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            File = resolve(File),
        };
}

internal sealed record SymbolsRequest(string? File, string? Query, bool Unused, Paging Page)
    : ToolRequest
{
    public override Paging Page { get; } = Page;

    public override CallPolicy Policy =>
        new(LspAction.Symbols, Files: Unused ? FileScope.CSharpOrGlob : FileScope.CSharp);

    public override IEnumerable<string?> FileNames => [File];

    public override SymbolsRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            File = resolve(File),
        };
}

/// <param name="ProposalId">Selects a pending proposal, whose own target replaces the request's.</param>
internal closed record EditRequest(string? ProposalId, bool Apply, Paging Page) : ToolRequest
{
    public override Paging Page { get; } = Page;

    public abstract LspAction Action { get; }

    protected virtual FileScope Files => FileScope.CSharp;

    // A new edit synchronizes Roslyn itself
    public override CallPolicy Policy =>
        new(Action, MayRestart: Apply, SynchronizesItself: ProposalId is null, Files: Files);
}

internal sealed record RenameRequest(
    SymbolTarget Target,
    string? NewName,
    string? ProposalId,
    bool Apply,
    Paging Page
) : EditRequest(ProposalId, Apply, Page)
{
    public override LspAction Action => LspAction.Rename;

    public override IEnumerable<string?> FileNames => [Target.File];

    public override RenameRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            Target = Target.WithFile(resolve),
        };
}

/// <param name="NewName">The workspace-relative destination.</param>
internal sealed record RenameFileRequest(
    string? File,
    string? NewName,
    string? ProposalId,
    bool Apply,
    Paging Page
) : EditRequest(ProposalId, Apply, Page)
{
    public override LspAction Action => LspAction.RenameFile;

    protected override FileScope Files => FileScope.AnyFile;

    public override IEnumerable<string?> FileNames => [File];

    public override RenameFileRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            File = resolve(File),
        };
}

/// <param name="Diagnostic">A diagnostic code whose fixes replace the selection.</param>
internal sealed record CodeActionsRequest(
    SymbolTarget Target,
    int? EndLine,
    int? EndCharacter,
    string? Diagnostic,
    int? ActionIndex,
    bool All,
    Paging Page,
    string? ProposalId,
    bool Apply
) : EditRequest(ProposalId, Apply, Page)
{
    public override LspAction Action => LspAction.CodeActions;

    public override IEnumerable<string?> FileNames => [Target.File];

    public override CodeActionsRequest WithFiles(Func<string?, string?> resolve) =>
        this with
        {
            Target = Target.WithFile(resolve),
        };
}

internal sealed record StatusRequest : ToolRequest
{
    public override CallPolicy Policy => new(LspAction.Status, StartsServer: false);
}

internal sealed record ReloadRequest : ToolRequest
{
    public override CallPolicy Policy =>
        new(LspAction.Reload, StartsServer: false, MayRestart: true);
}

internal sealed record CapabilitiesRequest : ToolRequest
{
    public override CallPolicy Policy => new(LspAction.Capabilities);
}

/// <param name="Apply">Allows a method that may change the workspace.</param>
internal sealed record RawLspRequest(string? Method, JsonObject? Parameters, bool Apply)
    : ToolRequest
{
    public override CallPolicy Policy => new(LspAction.Request, MayRestart: Apply);
}
