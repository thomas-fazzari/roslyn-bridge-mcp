// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;
using static RoslynBridge.Results.ResultText;

namespace RoslynBridge.Features.Navigate;

internal sealed record HoverText(string Text) : ToolResult
{
    public override bool IsEmpty => Text.Length == 0;

    internal override void Write(StringBuilder text, int budget) => text.Append(Text);
}

internal sealed record FileLocations(
    LocationFile File,
    IReadOnlyList<SourcePosition> Positions,
    IReadOnlyDictionary<int, string>? Lines = null
);

/// <param name="Generated">Source-generated occurrences left out.</param>
/// <param name="Access">Reads or Writes when the references are filtered to that access.</param>
internal sealed record Locations(
    IReadOnlyList<FileLocations> Files,
    int Total,
    int? NextOffset,
    int Generated,
    LspAction? Access = null
) : ToolResult
{
    public override bool IsEmpty => Total == 0 && Generated == 0;

    internal override void Write(StringBuilder text, int budget)
    {
        foreach (var file in Files)
        {
            text.Append(file.File.Text);
            if (file.Lines is { } lines)
            {
                text.Append('\n');
                AppendSourceLines(text, file.Positions, lines);
                continue;
            }

            text.Append(' ').AppendJoin(' ', file.Positions.Select(Position)).Append('\n');
        }

        if (this is { Total: 0, Access: { } access })
            text.Append("no ").Append(access.Name).Append('\n');
        else
            AppendPageEnd(text, Total, Files.Count == 0, NextOffset, "locations");

        if (Generated > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"generated code: {Generated} more omitted. Use lsp request to list them.\n"
            );
        }
    }
}

/// <param name="Accesses">Property, field and constant accesses left out of callees.</param>
/// <param name="Hierarchy">The action, when Roslyn found a symbol to expand.</param>
internal sealed record RelatedSymbols(
    IReadOnlyList<SymbolItem> Items,
    int Total,
    int? NextOffset,
    int Accesses = 0,
    LspAction? Hierarchy = null
) : ToolResult
{
    public override bool IsEmpty => Total == 0;

    internal override void Write(StringBuilder text, int budget)
    {
        foreach (var symbol in Items)
        {
            AppendSymbolTree(text, symbol, indent: string.Empty);
        }

        if (this is { Total: 0, Hierarchy: { } hierarchy })
            AppendNoRelated(text, hierarchy);
        else
            AppendPageEnd(text, Total, Items.Count == 0, NextOffset, "symbols");

        if (Accesses > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"{Accesses} property, field or constant accesses omitted\n"
            );
        }
    }

    private static void AppendNoRelated(StringBuilder text, LspAction hierarchy)
    {
        text.Append("no ").Append(hierarchy.Name).Append('\n');
        if (hierarchy is LspAction.Callers)
            text.Append("reflection or code outside the workspace may still call the member\n");
    }
}

internal sealed record SourcePart(SymbolItem Symbol, int FirstLine, int LastLine, string Text);

/// <param name="OmittedLines">Declaration lines left out to fit the response.</param>
internal sealed record SourceParts(IReadOnlyList<SourcePart> Parts, int OmittedLines) : ToolResult
{
    internal override void Write(StringBuilder text, int budget)
    {
        foreach (var part in Parts)
        {
            text.Append(part.Symbol.File?.Text)
                .Append(CultureInfo.InvariantCulture, $":{part.FirstLine}-{part.LastLine} ")
                .Append(part.Symbol.Kind)
                .Append(' ')
                .Append(part.Symbol.Name)
                .Append('\n')
                .Append(part.Text);
        }

        if (OmittedLines > 0)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"truncated: {OmittedLines} lines in declarations too large to show. Use members, then source on one member.\n"
            );
        }
    }
}

/// <param name="Members">Names are relative to the type.</param>
internal sealed record TypeMembers(
    SymbolItem Type,
    IReadOnlyList<SymbolItem> Members,
    int Total,
    int? NextOffset
) : ToolResult
{
    internal override void Write(StringBuilder text, int budget)
    {
        text.Append(Type.Kind).Append(' ').Append(Type.Name).Append('\n');
        LocationFile? file = null;
        string? baseClass = null;
        foreach (var member in Members)
        {
            if (
                member.Container is { } container
                && !string.Equals(container, baseClass, StringComparison.Ordinal)
            )
            {
                baseClass = container;
                file = null;
                text.Append("inherited from ").Append(container).Append('\n');
            }

            if (member.File != file)
            {
                file = member.File;
                text.Append(file?.Text).Append('\n');
            }

            text.Append(Indent)
                .Append(Position(member.Position!.Value))
                .Append(' ')
                .Append(member.Kind)
                .Append(' ')
                .Append(member.Name)
                .Append('\n');
        }

        AppendPageEnd(text, Total, Members.Count == 0, NextOffset, "members");
    }
}
