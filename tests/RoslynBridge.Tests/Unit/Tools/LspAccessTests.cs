// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LspAccessTests : IDisposable
{
    private const int TextKind = 1;
    private const int ReadKind = 2;
    private const int WriteKind = 3;
    private const string SourceFile = "Source.cs";

    private static readonly string[] _referencedFiles = ["A.cs", "B.cs"];

    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-lsp-access-").FullName;
    private readonly ConcurrentQueue<(string File, int Line)> _highlightRequests = new();

    // A.cs line 1 is a write, 2 a read and 3 a write. B.cs line 1 and 2 are reads, 3 is a declaration,
    // and line 4 has no highlight. Lines are zero-based, as in LSP.
    private readonly Dictionary<string, (int Line, int Kind)[]> _highlights = new(
        StringComparer.Ordinal
    )
    {
        ["A.cs"] = [(0, WriteKind), (1, ReadKind), (2, WriteKind)],
        ["B.cs"] = [(0, ReadKind), (1, ReadKind), (2, TextKind)],
    };

    [Theory]
    [InlineData("reads", "A.cs 2:1\nB.cs 1:1 2:1")]
    [InlineData("writes", "A.cs 1:1 3:1")]
    public async Task KeepsTheReferencesThatRoslynHighlightsWithTheWantedKindAsync(
        string action,
        string expected
    )
    {
        await WriteFilesAsync();
        await using var session = CreateSession(_root, HighlightServer());
        using var tool = CreateTool(_root, session);

        var response = await tool.ExecuteAsync(
            At(Enum.Parse<LspAction>(action, ignoreCase: true)),
            TestCt
        );

        ResponseText(response).Should().Be(expected);
        _highlightRequests.Should().BeEquivalentTo([("A.cs", 0), ("B.cs", 0)]);
    }

    [Fact]
    public async Task ShowsTheSourceLinesOfReadsWithContextAsync()
    {
        await WriteFilesAsync();
        await using var session = CreateSession(_root, HighlightServer());
        using var tool = CreateTool(_root, session);

        var response = await tool.ExecuteAsync(At(LspAction.Reads) with { Context = true }, TestCt);

        ResponseText(response).Should().Be("A.cs\n  2:1 a1\nB.cs\n  1:1 b0\n  2:1 b1");
    }

    [Theory]
    [InlineData("reads", "no reads")]
    [InlineData("writes", "no writes")]
    public async Task NamesWhatIsMissingWhenNoReferenceHasTheWantedKindAsync(
        string action,
        string expected
    )
    {
        await WriteFilesAsync();
        _highlights["A.cs"] = [(0, TextKind)];
        _highlights["B.cs"] = [];
        await using var session = CreateSession(_root, HighlightServer());
        using var tool = CreateTool(_root, session);

        var response = await tool.ExecuteAsync(
            At(Enum.Parse<LspAction>(action, ignoreCase: true)),
            TestCt
        );

        ResponseText(response).Should().Be(expected);
    }

    [Fact]
    public async Task PagesTheReadsAfterFilteringAsync()
    {
        await WriteFilesAsync();
        await using var session = CreateSession(_root, HighlightServer());
        using var tool = CreateTool(_root, session);
        var request = At(LspAction.Reads) with { Limit = 2 };

        var first = await tool.ExecuteAsync(request, TestCt);
        var last = await tool.ExecuteAsync(request with { Offset = 2 }, TestCt);
        var past = await tool.ExecuteAsync(request with { Offset = 9 }, TestCt);

        ResponseText(first)
            .Should()
            .StartWith("A.cs 2:1\nB.cs 1:1\ntruncated: 3 locations in total, next offset: 2.");
        ResponseText(last).Should().Be("B.cs 2:1");
        ResponseText(past).Should().Be("offset past the end: 3 locations in total");
    }

    [Fact]
    public async Task BulkRunsWritesLikeAnyOtherNavigationAsync()
    {
        await WriteFilesAsync();
        await using var session = CreateSession(_root, HighlightServer());
        using var tool = CreateTool(_root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Bulk,
                Queries = [At(LspAction.Writes), At(LspAction.Reads)],
            },
            TestCt
        );

        ResponseText(response)
            .Should()
            .Be(
                "## 1 writes Source.cs:1:1\nA.cs 1:1 3:1\n## 2 reads Source.cs:1:1\nA.cs 2:1\nB.cs 1:1 2:1"
            );
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task WriteFilesAsync()
    {
        await WriteFileAsync(_root, SourceFile, "class S {}");
        await WriteFileAsync(
            _root,
            "A.cs",
            """
            a0
            a1
            a2
            a3
            a4
            """
        );
        await WriteFileAsync(
            _root,
            "B.cs",
            """
            b0
            b1
            b2
            b3
            b4
            """
        );
    }

    private static LspRequest At(LspAction action) =>
        new()
        {
            Action = action,
            File = SourceFile,
            Line = 1,
            Character = 1,
        };

    private RoslynLauncher HighlightServer() =>
        FakeServer(server =>
            server.Handle = (method, parameters) =>
                Task.FromResult<JsonNode?>(
                    method switch
                    {
                        LspMethods.TextDocumentReferences => References(),
                        LspMethods.TextDocumentDocumentHighlight => Highlights(parameters!),
                        _ => null,
                    }
                )
        );

    private JsonArray References() =>
        new([
            .. _referencedFiles.SelectMany(file =>
                Enumerable.Range(0, file is "A.cs" ? 3 : 4).Select(line => Location(file, line))
            ),
        ]);

    private JsonArray Highlights(JsonNode parameters)
    {
        var file = Path.GetFileName(
            new Uri(parameters["textDocument"]!["uri"]!.GetValue<string>()).LocalPath
        );
        _highlightRequests.Enqueue((file, parameters["position"]!["line"]!.GetValue<int>()));
        return new JsonArray([
            .. _highlights[file]
                .Select(highlight => new JsonObject
                {
                    ["range"] = Range(highlight.Line),
                    ["kind"] = highlight.Kind,
                }),
        ]);
    }

    private JsonObject Location(string file, int line) =>
        new() { ["uri"] = new Uri(Path.Combine(_root, file)).AbsoluteUri, ["range"] = Range(line) };

    private static JsonObject Range(int line) =>
        new()
        {
            ["start"] = new JsonObject { ["line"] = line, ["character"] = 0 },
        };
}
