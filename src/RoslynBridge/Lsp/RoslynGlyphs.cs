// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Frozen;

namespace RoslynBridge.Lsp;

/// <summary>
/// Values of the <c>glyph</c> that Roslyn adds to document symbols, which encodes the kind and the accessibility.
/// </summary>
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

    /// <returns>Whether the glyph marks a private symbol. A missing glyph counts as not private.</returns>
    public static bool IsPrivate(int? glyph) => glyph is { } value && _private.Contains(value);
}
