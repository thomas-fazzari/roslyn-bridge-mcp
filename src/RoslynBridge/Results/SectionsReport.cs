// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

/// <param name="Result">The error when the section could not produce a result.</param>
internal sealed record ReportSection(string Label, Result<ToolResult> Result);

/// <param name="Heading">The Markdown heading marker of the sections. A nested report adds one level.</param>
internal sealed record SectionsReport(IReadOnlyList<ReportSection> Sections, string Heading = "##")
    : ToolResult
{
    // The largest sections are dropped first, so the smaller ones still fit the budget
    internal override void Write(StringBuilder text, int budget)
    {
        var blocks = Sections.Select(section => Block(section, budget)).ToArray();
        var sizes = blocks.Select(block => block.Length).ToArray();
        var omitted = new SortedSet<int>();

        while (
            omitted.Count < blocks.Length
            && blocks.Sum(block => block.Length) + OmissionNote(sizes, omitted).Length > budget
        )
        {
            var largest = Array.IndexOf(blocks, blocks.MaxBy(block => block.Length));
            blocks[largest] = string.Empty;
            omitted.Add(largest);
        }

        text.AppendJoin(string.Empty, blocks).Append(OmissionNote(sizes, omitted));
    }

    private string Block(ReportSection section, int budget)
    {
        var header = $"{Heading} {section.Label}";
        return section.Result.IsError(out var error, out var value)
            ? $"{header}\n{ResultText.Error(error)}\n"
            : value switch
            {
                { IsEmpty: true } => $"{header}: none\n",
                SectionsReport nested =>
                    $"{header}\n{(nested with { Heading = Heading + "#" }).Render(budget)}\n",
                _ => $"{header}\n{value.Render(budget)}\n",
            };
    }

    private string OmissionNote(int[] sizes, SortedSet<int> omitted) =>
        omitted.Count == 0
            ? string.Empty
            : new StringBuilder("omitted to fit: ")
                .AppendJoin(
                    ", ",
                    omitted.Select(index =>
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"{Sections[index].Label} ({sizes[index]:N0} chars)"
                        )
                    )
                )
                .Append(". Request them alone or in a smaller batch.\n")
                .ToString();
}
