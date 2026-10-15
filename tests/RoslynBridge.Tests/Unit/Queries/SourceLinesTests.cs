// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using RoslynBridge.Queries;
using RoslynBridge.Results;

namespace RoslynBridge.Tests.Unit.Queries;

public sealed class SourceLinesTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("source-lines-");

    public void Dispose() => _root.Delete(recursive: true);

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
        var symbols = new SymbolList(
            [
                new SymbolItem("Target", "method")
                {
                    File = "Callee.cs",
                    Position = new(1, 6),
                    Calls = [new(3, 5)],
                },
            ],
            Total: 1,
            Truncated: false
        );

        var result = SourceLines.Add(
            symbols,
            new WorkspacePaths(_root.FullName),
            Path.Combine(_root.FullName, "Caller.cs")
        );

        result
            .Items[0]
            .Lines.Should()
            .BeEquivalentTo(new Dictionary<int, string> { [3] = "Target();" });
    }

    [Fact]
    public void ShortensLongLines()
    {
        Write("Long.cs", new string('x', SourceLines.MaximumLineCharacters + 10));

        var lines = Lines("Long.cs", new SourcePosition(1, 1));

        lines![1].Should().HaveLength(SourceLines.MaximumLineCharacters + 1).And.EndWith("…");
    }

    [Fact]
    public void SkipsLocationsOutsideTheWorkspace() =>
        Lines("roslyn-source-generated://assembly/Generated.cs", new SourcePosition(1, 1))
            .Should()
            .BeNull();

    private void Write(string file, string text) =>
        File.WriteAllText(Path.Combine(_root.FullName, file), text);

    private IReadOnlyDictionary<int, string>? Lines(
        string file,
        params SourcePosition[] positions
    ) =>
        SourceLines
            .Add(
                new Locations([new FileLocations(file, positions)], positions.Length, false, 0),
                new WorkspacePaths(_root.FullName)
            )
            .Files[0]
            .Lines;
}
