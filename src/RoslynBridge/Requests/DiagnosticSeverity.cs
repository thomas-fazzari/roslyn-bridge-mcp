// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace RoslynBridge.Requests;

/// <summary>
/// LSP diagnostic severities, from most to least severe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiagnosticSeverity>))]
internal enum DiagnosticSeverity
{
    [JsonStringEnumMemberName("error")]
    Error = 1,

    [JsonStringEnumMemberName("warning")]
    Warning = 2,

    [JsonStringEnumMemberName("information")]
    Information = 3,

    [JsonStringEnumMemberName("hint")]
    Hint = 4,
}
