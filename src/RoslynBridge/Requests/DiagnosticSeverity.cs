// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Serialization;

namespace RoslynBridge.Requests;

/// <summary>
/// LSP diagnostic severities, from most to least severe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
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
