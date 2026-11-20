// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge;

internal enum LspAction
{
    Diagnostics = 0,

    Definition = 1,

    TypeDefinition = 2,

    Implementation = 3,

    References = 4,

    Hover = 5,

    Symbols = 6,

    Rename = 7,

    RenameFile = 8,

    CodeActions = 9,

    Status = 10,

    Reload = 11,

    Capabilities = 12,

    Request = 13,

    Callers = 14,

    Callees = 15,

    Supertypes = 16,

    Subtypes = 17,

    Source = 18,

    Members = 19,

    Impact = 20,

    Bulk = 21,

    Reads = 22,

    Writes = 23,
}

internal static class LspActions
{
    extension(LspAction action)
    {
        public string Name => JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString());
    }
}
