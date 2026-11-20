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
    public static ExtensionReferences NotFound { get; } = new(false, [], [], 0);
}

/// <param name="Calls">LSP incoming calls, which may also name the member they go through.</param>
internal sealed record ExtensionCallers(
    [property: JsonRequired] bool Found,
    [property: JsonRequired] LspCall[] Calls
) : IExtensionAnswer<ExtensionCallers>
{
    public static ExtensionCallers NotFound { get; } = new(false, []);
}
