// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Serialization;

namespace RoslynBridge.Editing;

[JsonConverter(typeof(JsonStringEnumConverter<EditApplicationPhase>))]
internal enum EditApplicationPhase
{
    [JsonStringEnumMemberName("write")]
    Write = 0,

    [JsonStringEnumMemberName("cleanup")]
    Cleanup = 1,

    [JsonStringEnumMemberName("command")]
    Command = 2,

    [JsonStringEnumMemberName("synchronize")]
    Synchronize = 3,
}
