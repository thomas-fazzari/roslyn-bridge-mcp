// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Lsp;

internal static class RoslynGlyphs
{
    // Values of Microsoft.CodeAnalysis.Glyph
    private static readonly FrozenSet<int> _private = FrozenSet.Create(
        6, // ClassPrivate
        12, // ConstantPrivate
        16, // DelegatePrivate
        20, // EnumPrivate
        24, // EnumMemberPrivate
        30, // EventPrivate
        34, // ExtensionMethodPrivate
        38, // FieldPrivate
        42, // InterfacePrivate
        51, // MethodPrivate
        55, // ModulePrivate
        60, // OperatorPrivate
        65, // PropertyPrivate
        71 // StructurePrivate
    );

    public static bool IsPrivate(int? glyph) => glyph is { } value && _private.Contains(value);
}
