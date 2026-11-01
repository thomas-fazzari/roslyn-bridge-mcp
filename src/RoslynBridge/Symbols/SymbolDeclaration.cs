// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Results;

namespace RoslynBridge.Symbols;

/// <param name="Name">Qualified name with its parameter list, which resolves back to this symbol.</param>
/// <param name="Signature">Roslyn's unqualified name, with the member type when it has one.</param>
/// <param name="Line">One-based. The position of the name.</param>
/// <param name="FirstLine">One-based first line of the whole declaration.</param>
/// <param name="IsPrivate">Roslyn marks an explicit interface implementation as private.</param>
[DebuggerDisplay("{Name} at {File}:{Line}:{Character}")]
internal sealed record SymbolDeclaration(
    string Name,
    string Signature,
    int? Kind,
    string File,
    int Line,
    int Character,
    int FirstLine,
    int LastLine,
    bool IsPrivate = false
)
{
    public SymbolItem ToItem(WorkspacePaths paths) =>
        new(Name, Kind is { } kind && LspSymbolKinds.TryGetName(kind, out var name) ? name : null)
        {
            File = paths.RelativePath(File),
            Position = new SourcePosition(Line, Character),
        };
}
