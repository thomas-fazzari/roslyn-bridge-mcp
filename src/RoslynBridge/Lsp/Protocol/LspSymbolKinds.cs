// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp.Protocol;

internal static class LspSymbolKinds
{
    private static readonly FrozenDictionary<LspSymbolKind, string> _names = new Dictionary<
        LspSymbolKind,
        string
    >
    {
        [LspSymbolKind.File] = "file",
        [LspSymbolKind.Module] = "module",
        [LspSymbolKind.Namespace] = "namespace",
        [LspSymbolKind.Package] = "package",
        [LspSymbolKind.Class] = "class",
        [LspSymbolKind.Method] = "method",
        [LspSymbolKind.Property] = "property",
        [LspSymbolKind.Field] = "field",
        [LspSymbolKind.Constructor] = "constructor",
        [LspSymbolKind.Enum] = "enum",
        [LspSymbolKind.Interface] = "interface",
        [LspSymbolKind.Function] = "function",
        [LspSymbolKind.Variable] = "variable",
        [LspSymbolKind.Constant] = "constant",
        [LspSymbolKind.String] = "string",
        [LspSymbolKind.Number] = "number",
        [LspSymbolKind.Boolean] = "boolean",
        [LspSymbolKind.Array] = "array",
        [LspSymbolKind.Object] = "object",
        [LspSymbolKind.Key] = "key",
        [LspSymbolKind.Null] = "null",
        [LspSymbolKind.EnumMember] = "enum_member",
        [LspSymbolKind.Struct] = "struct",
        [LspSymbolKind.Event] = "event",
        [LspSymbolKind.Operator] = "operator",
        [LspSymbolKind.TypeParameter] = "type_parameter",
    }.ToFrozenDictionary();

    /// <summary>
    /// Fails for a kind outside the LSP SymbolKind values, which a server may still send.
    /// </summary>
    public static bool TryGetName(LspSymbolKind kind, [NotNullWhen(true)] out string? name) =>
        _names.TryGetValue(kind, out name);

    public static bool IsInvocable(LspSymbolKind? kind) =>
        kind
            is LspSymbolKind.Method
                or LspSymbolKind.Constructor
                or LspSymbolKind.Function
                or LspSymbolKind.Operator;

    public static bool IsType(LspSymbolKind kind) =>
        kind
            is LspSymbolKind.Class
                or LspSymbolKind.Enum
                or LspSymbolKind.Interface
                or LspSymbolKind.Struct;
}
