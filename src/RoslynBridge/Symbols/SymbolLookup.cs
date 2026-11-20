// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Symbols;

/// <param name="PrefersTypes">A name resolves to a type before a member of the same name.</param>
/// <param name="ToDeclaration">A position on a usage moves to the declaration of its symbol.</param>
internal readonly record struct SymbolLookup(bool PrefersTypes, bool ToDeclaration)
{
    public static SymbolLookup AtPosition { get; } = new(PrefersTypes: false, ToDeclaration: false);
}
