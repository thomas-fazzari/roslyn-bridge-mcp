// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

/// <summary>
/// The answer of a Roslyn extension handler for a document position.
/// </summary>
internal interface IExtensionAnswer<TSelf>
    where TSelf : IExtensionAnswer<TSelf>
{
    /// <summary>
    /// The answer that sends the caller to the LSP request.
    /// </summary>
    static abstract TSelf NotFound { get; }

    /// <summary>
    /// False when the caller must ask the LSP request instead.
    /// </summary>
    bool Found { get; }
}

/// <summary>
/// The answer of a dispatched extension message.
/// </summary>
/// <param name="Response">The JSON answer of the handler, empty when it threw.</param>
internal sealed record ExtensionDispatch(
    string? Response = null,
    ExtensionFailure? ExtensionException = null,
    bool ExtensionWasUnloaded = false
)
{
    public static ExtensionDispatch None { get; } = new();

    public string? Answer => string.IsNullOrWhiteSpace(Response) ? null : Response;

    public string Error =>
        ExtensionException?.Message
        ?? (ExtensionWasUnloaded ? "Roslyn unloaded the extension." : "Roslyn returned no result.");
}

/// <summary>
/// The answer of the extension activation, with the message names Roslyn registered.
/// </summary>
internal sealed record ExtensionActivation(
    string[]? DocumentMessageHandlers = null,
    string[]? WorkspaceMessageHandlers = null,
    ExtensionFailure? ExtensionException = null
)
{
    public static ExtensionActivation None { get; } = new();
}

internal sealed record ExtensionFailure(
    [property: JsonPropertyName("Message")] string? Message = null
);

/// <param name="Written">The locations that documentHighlight marks as writes.</param>
/// <param name="Generated">References in source-generated documents, which are only counted.</param>
internal sealed record ExtensionReferences(
    [property: JsonRequired] bool Found,
    [property: JsonRequired] LspLocation[] Locations,
    [property: JsonRequired] LspLocation[] Written,
    [property: JsonRequired] int Generated
) : IExtensionAnswer<ExtensionReferences>
{
    public static ExtensionReferences NotFound { get; } = new(Found: false, [], [], 0);
}

/// <param name="Found">False when the position has no expression and declares no name. The caller has no fallback.</param>
/// <param name="Text">The expression, or the declared name.</param>
/// <param name="Kind">The syntax kind of the expression, or of the declaration.</param>
/// <param name="Declaration">True when the position is on the name that a declaration introduces.</param>
/// <param name="Nullability">The annotation and the flow state, such as NotAnnotated/NotNull.</param>
/// <param name="Conversion">Absent for an identity conversion.</param>
/// <param name="Constant">Absent when the expression is not a constant.</param>
/// <param name="Symbol">The original definition of the bound symbol.</param>
/// <param name="Instantiation">The constructed generic or reduced extension method that the code uses.</param>
/// <param name="UnboundReason">The Roslyn CandidateReason of an expression that binds to no symbol.</param>
internal sealed record ExtensionExpression(
    [property: JsonRequired] bool Found,
    [property: JsonRequired] string Text,
    [property: JsonRequired] string Kind,
    bool Declaration = false,
    string? Type = null,
    string? ConvertedType = null,
    string? Nullability = null,
    string? Conversion = null,
    string? Constant = null,
    LspSymbol? Symbol = null,
    string? Instantiation = null,
    string? UnboundReason = null,
    LspSymbol[]? Candidates = null,
    string? Documentation = null
) : IExtensionAnswer<ExtensionExpression>
{
    public static ExtensionExpression NotFound { get; } =
        new(Found: false, string.Empty, string.Empty);
}

/// <param name="Calls">
/// LSP incoming calls for callers, which may also name the member they go through, and outgoing calls for callees.
/// </param>
internal sealed record ExtensionCalls(
    [property: JsonRequired] bool Found,
    [property: JsonRequired] LspCall[] Calls
) : IExtensionAnswer<ExtensionCalls>
{
    public static ExtensionCalls NotFound { get; } = new(Found: false, []);
}
