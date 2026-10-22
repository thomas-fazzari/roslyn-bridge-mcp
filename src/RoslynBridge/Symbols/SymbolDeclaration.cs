// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Symbols;

/// <summary>
/// A declaration found for a symbol name, at a one-based position in a workspace file.
/// </summary>
/// <param name="Name">Qualified name with its parameter list, which resolves back to this symbol.</param>
/// <param name="Signature">Roslyn's unqualified name, with the member type when it has one.</param>
/// <param name="FirstLine">One-based first line of the whole declaration.</param>
/// <param name="LastLine">One-based last line of the whole declaration.</param>
[DebuggerDisplay("{Name} at {File}:{Line}:{Character}")]
internal sealed record SymbolDeclaration(
    string Name,
    string Signature,
    int? Kind,
    string File,
    int Line,
    int Character,
    int FirstLine,
    int LastLine
)
{
    /// <summary>
    /// Returns the symbol as results show it, with a workspace-relative file.
    /// </summary>
    public SymbolItem ToItem(WorkspacePaths paths) =>
        new(Name, Kind is { } kind && LspSymbolKinds.TryGetName(kind, out var name) ? name : null)
        {
            File = paths.RelativePath(File),
            Position = new SourcePosition(Line, Character),
        };
}
