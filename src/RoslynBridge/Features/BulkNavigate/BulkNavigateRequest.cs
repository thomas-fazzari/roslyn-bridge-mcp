// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.ObjectModel;
using RoslynBridge.Requests;

namespace RoslynBridge.Features.BulkNavigate;

/// <param name="Queries">The queries as the call sends them, so an invalid one fails only its section.</param>
internal sealed record BulkNavigateRequest(
    IReadOnlyList<BulkQuery> Queries,
    bool Context,
    Paging Page
) : ToolRequest
{
    internal const int MAXIMUM_QUERIES = 16;

    public override Paging Page { get; } = Page;

    /// <summary>
    /// The errors of query files that match several files, by name.
    /// </summary>
    public IReadOnlyDictionary<string, ToolError> FileErrors { get; init; } =
        ReadOnlyDictionary<string, ToolError>.Empty;

    public override CallPolicy Policy => new(LspAction.Bulk);

    public static Result<BulkNavigateRequest> From(
        IReadOnlyList<BulkQuery> queries,
        bool context,
        Paging page
    ) =>
        queries is { Count: < 1 or > MAXIMUM_QUERIES }
            ? ToolError.InvalidRequest($"Send between 1 and {MAXIMUM_QUERIES} queries.")
            : new BulkNavigateRequest(queries, context, page);

    public override BulkNavigateRequest WithFiles(FileResolver resolve) =>
        this with
        {
            Queries = [.. Queries.Select(query => query with { File = resolve(query.File) })],
        };

    public override Result<ToolRequest> WithFileErrors(
        IReadOnlyList<(string Name, ToolError Error)> errors
    ) =>
        this with
        {
            FileErrors = errors.ToDictionary(
                static error => error.Name,
                static error => error.Error,
                StringComparer.Ordinal
            ),
        };
}
