// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;

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
    public JsonObject ToJson(WorkspacePaths paths)
    {
        var json = new JsonObject { ["symbol"] = Name };
        if (Kind is { } kind && LspSymbolKinds.TryGetName(kind, out var kindName))
        {
            json["kind"] = kindName;
        }

        json["file"] = paths.RelativePath(File);
        json["position"] = new JsonArray(Line, Character);
        return json;
    }
}
