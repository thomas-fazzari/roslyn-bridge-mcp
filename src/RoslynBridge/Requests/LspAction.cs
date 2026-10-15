// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Requests;

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
}
