// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

internal sealed record ConfigurationParams(JsonElement[] Items);

internal sealed record ApplyWorkspaceEditParams(JsonObject Edit);

internal sealed record ApplyWorkspaceEditResult(bool Applied, string? FailureReason = null);

internal sealed record Registration(string Id, string Method);

internal sealed record RegistrationParams(Registration[] Registrations);

// The misspelling is part of the LSP 3.17 protocol
internal sealed record UnregistrationParams(
    [property: JsonPropertyName("unregisterations")] Registration[] Unregisterations
);

internal sealed record ProgressParams(JsonElement Token, WorkDoneProgress Value);

internal sealed record WorkDoneProgress(string Kind, string? Title = null, int? Percentage = null);

internal sealed record MessageParams(string Message);
