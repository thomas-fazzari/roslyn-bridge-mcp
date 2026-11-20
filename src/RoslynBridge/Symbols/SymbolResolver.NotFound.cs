// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Fastenshtein;
using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Symbols;

// Explains a name that matches no declaration, with the nearest candidates
internal sealed partial class SymbolResolver
{
    private const int MaximumSuggestions = 10;
    private const int MaximumTypoDistance = 3;

    // One shared word, such as Get, matches too many members
    private const int MinimumSharedWords = 2;

    private static readonly Result<ContainerLookup?> _noContainer = (ContainerLookup?)null;

    // An unreadable candidate file must not hide the error that the suggestions explain
    // A failed Roslyn request has an RPC code, so it reaches the caller
    private static bool IsUnreadable(ToolError error) =>
        error
            is { Code: ErrorCodes.Io or ErrorCodes.InvalidRequest }
                or { Code: ErrorCodes.Lsp, RpcCode: null };

    /// <summary>
    /// Returns the error of a failed Roslyn request in place of the missing symbol.
    /// </summary>
    private async Task<ToolError> NotFoundAsync(
        string text,
        SymbolName requested,
        string name,
        List<WorkspaceMatch>? matches,
        CancellationToken cancellationToken
    )
    {
        if (matches is null)
        {
            var workspaceMatches = await WorkspaceMatchesAsync(name, cancellationToken);
            if (workspaceMatches.IsError(out var matchError, out matches))
                return matchError;
        }

        var containerLookup = await ContainerAsync(requested, name, matches, cancellationToken);
        if (containerLookup.IsError(out var containerError, out var container))
            return containerError;

        var typeExists = container is { SimilarTypes: null };
        // From other types, only a member with the exact name suggests the caller named the wrong type
        var suggested = await SuggestionsAsync(
            name,
            container?.Members ?? [],
            typeExists ? [.. matches.Where(match => IsNamed(match, name))] : matches,
            cancellationToken
        );
        if (suggested.IsError(out var suggestionsError, out var suggestions))
            return suggestionsError;

        if (container is not null)
        {
            var display = string.Join('.', container.Name.Segments.Select(SymbolName.BaseName));
            // When only the parameters differ, the type has the member
            if (typeExists && !container.Members.Exists(member => IsNamed(member, name)))
            {
                return new ToolError(
                    ErrorCodes.SymbolNotFound,
                    suggestions.Count == 0
                        ? $"No declaration matches '{text}' because '{display}' has no member '{name}'. Use members to list its members."
                        : $"No declaration matches '{text}' because '{display}' has no member '{name}'. Send one candidate symbol instead.",
                    suggestions
                );
            }

            if (
                container.SimilarTypes is { } similar
                && suggestions.Count > 0
                && !suggestions.Exists(suggestion => IsInside(suggestion, container.Name))
            )
            {
                var types =
                    similar.Count == 0
                        ? string.Empty
                        : $" Similar types: {string.Join(", ", similar.Select(type => type.Name).Distinct(StringComparer.Ordinal))}.";
                return new ToolError(
                    ErrorCodes.SymbolNotFound,
                    $"No declaration matches '{text}' because no type matches '{display}'.{types} Use symbols to search by name."
                );
            }
        }

        return new ToolError(
            ErrorCodes.SymbolNotFound,
            suggestions.Count == 0
                ? $"No declaration matches '{text}'. Use symbols to search by name."
                : $"No declaration matches '{text}'. Send one candidate symbol instead.",
            suggestions
        );
    }

    private async Task<Result<ContainerLookup?>> ContainerAsync(
        SymbolName requested,
        string name,
        List<WorkspaceMatch> matches,
        CancellationToken cancellationToken
    )
    {
        if (requested.Segments.Length == 1)
            return _noContainer;

        var container = new SymbolName([.. requested.Segments[..^1]], Parameters: null);
        var containerName = SymbolName.BaseName(container.Segments[^1]);
        var candidateLookup = await WorkspaceMatchesAsync(containerName, cancellationToken);
        if (candidateLookup.IsError(out var error, out var candidates))
            return error;

        var symbolFiles = await DocumentSymbolsAsync(
            FilesNamed(candidates, containerName),
            cancellationToken
        );
        if (symbolFiles.IsError(out var documentsError, out var documents))
            return IsUnreadable(documentsError) ? _noContainer : documentsError;

        var types = FindDeclarations(documents, container).ToList();
        if (types.Count > 0)
        {
            var memberLookup = await MemberMatchesAsync(types, cancellationToken);
            if (memberLookup.IsError(out var membersError, out var members))
                return IsUnreadable(membersError) ? _noContainer : membersError;

            return new ContainerLookup(container, members, SimilarTypes: null);
        }

        // The container may be a namespace, which declares no members of its own
        if (matches.Exists(match => IsNamed(match, name)))
            return _noContainer;

        return (
            await SuggestionsAsync(
                containerName,
                [],
                [.. candidates.Where(match => LspSymbolKinds.IsType(match.Kind))],
                cancellationToken
            )
        ).Map<ContainerLookup?>(similar => new ContainerLookup(container, [], similar));
    }

    private static bool IsInside(SymbolItem suggestion, SymbolName container) =>
        SymbolName.Parse(suggestion.Name) is { } name
        && Enumerable
            .Range(1, name.Segments.Length - 1)
            .Any(length =>
                container.Selects(
                    new SymbolName([.. name.Segments[..length]], Parameters: null),
                    exact: false
                )
            );

    private async Task<Result<List<WorkspaceMatch>>> MemberMatchesAsync(
        List<SymbolDeclaration> types,
        CancellationToken cancellationToken
    )
    {
        var members = new List<WorkspaceMatch>();
        foreach (var type in types.GroupBy(type => type.Name, StringComparer.Ordinal))
        {
            if (
                (await MembersAsync([.. type], cancellationToken)).IsError(
                    out var error,
                    out var declared
                )
            )
            {
                return error;
            }

            members.AddRange(declared.Select(WorkspaceMatch.Of));
        }

        return members;
    }

    private async Task<Result<List<SymbolItem>>> SuggestionsAsync(
        string name,
        IEnumerable<WorkspaceMatch> preferred,
        List<WorkspaceMatch> matches,
        CancellationToken cancellationToken
    )
    {
        var close = Nearest(preferred, name, byWords: false).ToList();
        // Shared words help only when no member is close in spelling
        var nearest = (close.Count > 0 ? close : Nearest(preferred, name, byWords: true))
            .Concat(Nearest(matches, name, byWords: false))
            .Distinct()
            .Take(MaximumSuggestions)
            .ToList();
        var lookup = await DeclarationsAtAsync(nearest, cancellationToken);
        if (lookup.IsError(out var error, out var declarations))
            return IsUnreadable(error) ? new List<SymbolItem>() : error;

        return nearest
            .Select(position => declarations.GetValueOrDefault(position))
            .OfType<SymbolDeclaration>()
            .Select(declaration => declaration.ToItem(paths))
            .ToList();
    }

    private static IEnumerable<SourceLocation> Nearest(
        IEnumerable<WorkspaceMatch> matches,
        string name,
        bool byWords
    ) =>
        matches
            .DistinctBy(match => match.Location)
            .Select(match => match.Resembling(name, byWords))
            .OfType<Suggestion>()
            .Order(Suggestion.Nearest)
            .Select(suggestion => suggestion.Match.Location);

    private enum NameResemblance
    {
        SameName = 0,
        ContainsName = 1,
        Typo = 2,
        SharedWords = 3,
    }

    private readonly record struct WorkspaceMatch(string Name, SourceLocation Location, int? Kind)
    {
        public static WorkspaceMatch Of(SymbolDeclaration declaration) =>
            new(
                SymbolName.BaseName(SymbolName.Parse(declaration.Name)!.Segments[^1]),
                declaration.Location,
                declaration.Kind
            );

        /// <param name="byWords">
        /// Also accepts a name that shares most words, such as OnPlaybackStart for ReportPlaybackStart.
        /// </param>
        public Suggestion? Resembling(string requested, bool byWords)
        {
            var distance = Levenshtein.Distance(
                requested.ToLowerInvariant(),
                Name.ToLowerInvariant()
            );
            if (string.Equals(Name, requested, StringComparison.OrdinalIgnoreCase))
                return new Suggestion(this, NameResemblance.SameName, distance);

            if (Name.Contains(requested, StringComparison.OrdinalIgnoreCase))
                return new Suggestion(this, NameResemblance.ContainsName, distance);

            if (distance <= Math.Min(MaximumTypoDistance, requested.Length / 3))
                return new Suggestion(this, NameResemblance.Typo, distance);

            return byWords && SharesMostWords(requested, Name)
                ? new Suggestion(this, NameResemblance.SharedWords, distance)
                : null;
        }

        private static bool SharesMostWords(string requested, string name)
        {
            var words = Words(requested);
            var shared = words.Count(Words(name).Contains);
            return shared >= MinimumSharedWords && shared * 2 >= words.Count;
        }

        // PascalCase words
        private static HashSet<string> Words(string name)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var start = 0;
            for (var index = 1; index <= name.Length; index++)
            {
                if (index == name.Length || char.IsUpper(name[index]))
                {
                    words.Add(name[start..index]);
                    start = index;
                }
            }

            return words;
        }
    }

    private readonly record struct Suggestion(
        WorkspaceMatch Match,
        NameResemblance Kind,
        int Distance
    )
    {
        public static IComparer<Suggestion> Nearest { get; } = Comparer<Suggestion>.Create(Compare);

        private static int Compare(Suggestion first, Suggestion second)
        {
            var comparison = first.Kind.CompareTo(second.Kind);
            if (comparison != 0)
                return comparison;

            comparison = first.Distance.CompareTo(second.Distance);
            if (comparison != 0)
                return comparison;

            comparison = string.CompareOrdinal(first.Match.Name, second.Match.Name);
            return comparison != 0
                ? comparison
                : SourceLocation.Order.Compare(first.Match.Location, second.Match.Location);
        }
    }

    private sealed record ContainerLookup(
        SymbolName Name,
        List<WorkspaceMatch> Members,
        List<SymbolItem>? SimilarTypes
    );
}
