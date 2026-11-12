// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace RoslynBridge.Extension;

/// <summary>
/// Finds references as textDocument/references does, without the 500 ms batching delay of its handler.
/// </summary>
internal sealed class ReferencesHandler
    : IExtensionDocumentMessageHandler<PositionRequest, ReferencesResponse>
{
    private static readonly ReferencesResponse _notFound = new(Found: false, [], [], Generated: 0);

    private readonly MethodInfo _featureOptions;
    private readonly MethodInfo _findReferences;
    private readonly PropertyInfo _isWrittenTo;

    /// <summary>
    /// Throws when Roslyn lacks the internal search, so the extension fails to activate.
    /// </summary>
    public ReferencesHandler()
    {
        // Public SymbolFinder searches cascade to sibling overrides, unlike the textDocument/references handler
        var options = typeof(SymbolFinder).Assembly.GetType(
            "Microsoft.CodeAnalysis.FindSymbols.FindReferencesSearchOptions"
        ) ?? throw new MissingMemberException("FindReferencesSearchOptions");
        _featureOptions = options.GetMethod(
            "GetFeatureOptionsForStartingSymbol",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(ISymbol)]
        ) ?? throw new MissingMethodException(options.Name, "GetFeatureOptionsForStartingSymbol");
        _findReferences = typeof(SymbolFinder).GetMethod(
            nameof(SymbolFinder.FindReferencesAsync),
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(ISymbol), typeof(Solution), options, typeof(CancellationToken)]
        ) ?? throw new MissingMethodException(nameof(SymbolFinder), "FindReferencesAsync");
        // documentHighlight tells reads from writes with this internal property
        _isWrittenTo = typeof(ReferenceLocation).GetProperty(
            "IsWrittenTo",
            BindingFlags.NonPublic | BindingFlags.Instance
        ) ?? throw new MissingMemberException(nameof(ReferenceLocation), "IsWrittenTo");
    }

    public async Task<ReferencesResponse> ExecuteAsync(
        PositionRequest message,
        ExtensionDocumentMessageContext context,
        CancellationToken cancellationToken
    )
    {
        if (context.Solution.GetDocument(context.TextDocument.Id) is not { } document)
        {
            return _notFound;
        }

        if (await document.GetPositionAsync(message, cancellationToken) is not { } position)
        {
            return _notFound;
        }

        // On a literal, Roslyn lists its occurrences instead of references
        if (await TouchesLiteralAsync(document, position, cancellationToken))
        {
            return _notFound;
        }

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(
            document,
            position,
            cancellationToken
        );
        if (symbol is null)
        {
            return _notFound;
        }

        var referenced = await FindReferencesAsync(
            symbol,
            document.Project.Solution,
            cancellationToken
        );
        var seen = new HashSet<(string?, TextSpan)>();
        var locations = new List<LspLocation>();
        var written = new List<LspLocation>();
        var generated = 0;
        foreach (
            var reference in referenced.SelectMany(symbolReferences => symbolReferences.Locations)
        )
        {
            var location = reference.Location;
            if (
                !location.IsVisibleSourceLocation(cancellationToken)
                || !seen.Add((reference.Document.FilePath, location.SourceSpan))
            )
            {
                continue;
            }

            if (reference.Document is SourceGeneratedDocument)
            {
                generated++;
                continue;
            }

            if (reference.Document.FilePath is not { } filePath)
            {
                continue;
            }

            var found = new LspLocation(filePath.ToLspUri(), location.ToLspRange());
            locations.Add(found);
            if (_isWrittenTo.GetValue(reference) is true)
            {
                written.Add(found);
            }
        }

        return new ReferencesResponse(Found: true, locations, written, generated);
    }

    private Task<ImmutableArray<ReferencedSymbol>> FindReferencesAsync(
        ISymbol symbol,
        Solution solution,
        CancellationToken cancellationToken
    ) =>
        (Task<ImmutableArray<ReferencedSymbol>>)
            _findReferences.Invoke(
                null,
                [symbol, solution, _featureOptions.Invoke(null, [symbol]), cancellationToken]
            )!;

    private static async Task<bool> TouchesLiteralAsync(
        Document document,
        int position,
        CancellationToken cancellationToken
    )
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root is null)
        {
            return false;
        }

        return root.FindToken(position).IsLiteralTouching(position)
            || (position > 0 && root.FindToken(position - 1).IsLiteralTouching(position));
    }
}
