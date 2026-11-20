// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Features.Navigate;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum NavigationAction
{
    [JsonStringEnumMemberName("definition")]
    Definition = 0,

    [JsonStringEnumMemberName("type_definition")]
    TypeDefinition = 1,

    [JsonStringEnumMemberName("implementation")]
    Implementation = 2,

    [JsonStringEnumMemberName("references")]
    References = 3,

    [JsonStringEnumMemberName("hover")]
    Hover = 4,

    [JsonStringEnumMemberName("callers")]
    Callers = 5,

    [JsonStringEnumMemberName("callees")]
    Callees = 6,

    [JsonStringEnumMemberName("supertypes")]
    Supertypes = 7,

    [JsonStringEnumMemberName("subtypes")]
    Subtypes = 8,

    [JsonStringEnumMemberName("source")]
    Source = 9,

    [JsonStringEnumMemberName("members")]
    Members = 10,

    [JsonStringEnumMemberName("impact")]
    Impact = 11,

    [JsonStringEnumMemberName("reads")]
    Reads = 12,

    [JsonStringEnumMemberName("writes")]
    Writes = 13,
}

internal static class NavigationActions
{
    extension(NavigationAction action)
    {
        public LspAction ToLspAction() =>
            action switch
            {
                NavigationAction.Definition => LspAction.Definition,
                NavigationAction.TypeDefinition => LspAction.TypeDefinition,
                NavigationAction.Implementation => LspAction.Implementation,
                NavigationAction.References => LspAction.References,
                NavigationAction.Hover => LspAction.Hover,
                NavigationAction.Callers => LspAction.Callers,
                NavigationAction.Callees => LspAction.Callees,
                NavigationAction.Supertypes => LspAction.Supertypes,
                NavigationAction.Subtypes => LspAction.Subtypes,
                NavigationAction.Source => LspAction.Source,
                NavigationAction.Members => LspAction.Members,
                NavigationAction.Impact => LspAction.Impact,
                NavigationAction.Reads => LspAction.Reads,
                NavigationAction.Writes => LspAction.Writes,
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
    }
}
