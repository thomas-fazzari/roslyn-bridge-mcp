// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace RoslynBridge.Lsp;

/// <summary>
/// Names of the LSP SymbolKind values.
/// </summary>
internal static class LspSymbolKinds
{
    internal const int Namespace = 3;
    private const int Method = 6;
    private const int Constructor = 9;
    private const int Function = 12;
    private const int Operator = 25;

    private static readonly FrozenDictionary<int, string> _names = new Dictionary<int, string>
    {
        [1] = "file",
        [2] = "module",
        [Namespace] = "namespace",
        [4] = "package",
        [5] = "class",
        [Method] = "method",
        [7] = "property",
        [8] = "field",
        [Constructor] = "constructor",
        [10] = "enum",
        [11] = "interface",
        [Function] = "function",
        [13] = "variable",
        [14] = "constant",
        [15] = "string",
        [16] = "number",
        [17] = "boolean",
        [18] = "array",
        [19] = "object",
        [20] = "key",
        [21] = "null",
        [22] = "enum_member",
        [23] = "struct",
        [24] = "event",
        [Operator] = "operator",
        [26] = "type_parameter",
    }.ToFrozenDictionary();

    public static bool TryGetName(int kind, [NotNullWhen(true)] out string? name) =>
        _names.TryGetValue(kind, out name);

    public static bool IsInvocable(int? kind) =>
        kind is Method or Constructor or Function or Operator;
}
