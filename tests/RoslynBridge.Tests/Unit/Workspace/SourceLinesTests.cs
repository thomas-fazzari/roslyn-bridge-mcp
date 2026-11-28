// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Features.Navigate;
using RoslynBridge.Workspace;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Workspace;

public sealed class SourceLinesTests : TempWorkspaceTests
{
    [Fact]
    public void AddsEachOccurrenceLineOnceAndTrimmed()
    {
        Write("A.cs", "class A\n{\n    void M() => M() + M();\n}\n");

        var lines = Lines(
            "A.cs",
            new SourcePosition(3, 10),
            new SourcePosition(3, 17),
            new SourcePosition(1, 7)
        );

        lines
            .Should()
            .BeEquivalentTo(
                new Dictionary<int, string> { [1] = "class A", [3] = "void M() => M() + M();" }
            );
    }

    [Fact]
    public void CountsTheLineBreaksThatLspPositionsCount()
    {
        Write("Breaks.cs", "class A\u2028{\u0085    void M() { }\u2029}\n");

        var lines = Lines("Breaks.cs", new SourcePosition(3, 10), new SourcePosition(4, 1));

        lines
            .Should()
            .BeEquivalentTo(new Dictionary<int, string> { [3] = "void M() { }", [4] = "}" });
    }

    [Fact]
    public void ReadsCallSitesFromTheCallSiteFile()
    {
        Write("Callee.cs", "void Target() { }\n");
        Write("Caller.cs", "void Source()\n{\n    Target();\n}\n");
        var symbols = new RelatedSymbols(
            [
                new SymbolItem("Target", "method")
                {
                    File = new LocationFile.Workspace(Resolved(Root, "Callee.cs")),
                    Position = new(1, 6),
                    Calls = [new(3, 5)],
                },
            ],
            Total: 1,
            NextOffset: null
        );

        var result = symbols.WithSourceLines(Resolved(Root, "Caller.cs"));

        result
            .Items[0]
            .Lines.Should()
            .BeEquivalentTo(new Dictionary<int, string> { [3] = "Target();" });
    }

    [Fact]
    public void ShortensLongLines()
    {
        Write("Long.cs", new string('x', SourceLines.MAXIMUM_LINE_CHARACTERS + 10));

        var lines = Lines("Long.cs", new SourcePosition(1, 1));

        lines![1].Should().HaveLength(SourceLines.MAXIMUM_LINE_CHARACTERS + 1).And.EndWith("…");
    }

    private void Write(string file, string text) =>
        File.WriteAllText(Path.Combine(Root, file), text);

    private Dictionary<int, string>? Lines(string file, params SourcePosition[] positions) =>
        new SourceLines().At(Resolved(Root, file), positions);
}
