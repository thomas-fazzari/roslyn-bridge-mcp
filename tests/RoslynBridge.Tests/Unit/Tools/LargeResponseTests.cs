// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using static RoslynBridge.Tests.Fakes.LspLocationJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LargeResponseTests : TempWorkspaceTests
{
    private const string SourceText = "class A {}\n";
    private const string TruncatedPrefix = "truncated: ";

    [Fact]
    public async Task LargePreviewDropsTrailingFilesAndTheCommandToFitAsync()
    {
        const int fileCount = 100;
        const int lineCount = 50;
        // Short lines cost more as text than as JSON, so the budget must hold for rendered text
        var changes = new JsonObject();
        for (var index = 0; index < fileCount; index++)
        {
            var path = await WriteFileAsync(
                Root,
                string.Create(CultureInfo.InvariantCulture, $"Short{index}.cs"),
                string.Concat(Enumerable.Repeat("<\n", lineCount))
            );
            changes[new Uri(path).AbsoluteUri] = new JsonArray(
                TextEdit(0, 0, lineCount, 0, string.Concat(Enumerable.Repeat(">\n", lineCount)))
            );
        }

        var action = new JsonObject
        {
            ["title"] = "Change every file",
            ["edit"] = new JsonObject { ["changes"] = changes },
            ["command"] = new JsonObject
            {
                ["command"] = "test.command",
                ["arguments"] = new JsonArray(
                    new string('x', ResultText.MaximumResponseCharacters)
                ),
            },
        };
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentCodeAction
                            ? new JsonArray(action.DeepClone())
                            : null
                    )
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.CodeActions,
                File = "Short0.cs",
                Line = 1,
                Character = 1,
                ActionIndex = 0,
            },
            TestCt
        );

        response.Should().BeSuccessful();
        var text = ResponseText(response);
        var lines = ResultLines.Lines(text);
        lines[0].Should().EndWith($": {fileCount} files, not applied");
        lines
            .Should()
            .Contain("command omitted: too large to show")
            .And.Contain(line =>
                line.StartsWith(TruncatedPrefix, StringComparison.Ordinal)
                && line.EndsWith($" of {fileCount} files shown", StringComparison.Ordinal)
            );
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
    }

    [Fact]
    public async Task ReadOnlyResultOverTheLimitIsReportedAsTooLargeAsync()
    {
        await WriteFileAsync(Root, SourceFile, SourceText);
        // One location per file, so each location takes a line of about 160 characters
        var locations = new JsonArray([
            .. Enumerable
                .Range(0, LspRequest.MaximumResultLimit)
                .Select(index =>
                    Location(
                        Path.Combine(
                            Root,
                            new string('d', 150)
                                + string.Create(CultureInfo.InvariantCulture, $"{index}.cs")
                        )
                    )
                ),
        ]);
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentReferences ? locations.DeepClone() : null
                    )
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.References,
                File = SourceFile,
                Line = 1,
                Character = 7,
                Limit = LspRequest.MaximumResultLimit,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.ResultTooLarge);
    }

    [Fact]
    public async Task LargeCommandResultIsOmittedAndTheAppliedFilesAreKeptAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = async (method, _) =>
                {
                    if (method is not LspMethods.WorkspaceExecuteCommand)
                    {
                        return null;
                    }

                    await server.RequestClientAsync(
                        LspMethods.WorkspaceApplyEdit,
                        new JsonObject
                        {
                            ["edit"] = WorkspaceChanges(source, TextEdit(0, 6, 0, 7, "B")),
                        },
                        TestCt
                    );
                    return JsonValue.Create(new string('x', ResultText.MaximumResponseCharacters));
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = LspMethods.WorkspaceExecuteCommand,
                Parameters = new JsonObject { ["command"] = "test.command" },
                Apply = true,
            },
            TestCt
        );

        response.Should().BeSuccessful();
        var text = ResponseText(response);
        ResultLines
            .Lines(text)
            .Should()
            .StartWith("applied: 1 file, 1 operation, Roslyn synchronized")
            .And.Contain($"change {SourceFile}")
            .And.Contain("command executed, result omitted: too large to show");
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
        await AssertFileTextAsync(source, "class B {}\n");
    }

    [Fact]
    public async Task LargeReceiptKeepsExactPathsAndTheTotalCountsAsync()
    {
        const int fileCount = 200;
        // Each changed file takes a receipt line of about 210 characters
        var folder = new string('é', 100);
        var files = Enumerable
            .Range(0, fileCount)
            .Select(index =>
                $"{folder}/{new string('é', 100)}{index.ToString(CultureInfo.InvariantCulture)}.cs"
            )
            .ToList();
        var changes = new JsonObject();
        foreach (var file in files)
        {
            var path = await WriteFileAsync(Root, file, SourceText);
            changes[new Uri(path).AbsoluteUri] = new JsonArray(TextEdit(0, 6, 0, 7, "B"));
        }

        await WriteFileAsync(Root, SourceFile, SourceText);
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentRename
                            ? new JsonObject { ["changes"] = changes.DeepClone() }
                            : null
                    )
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                File = SourceFile,
                Line = 1,
                Character = 7,
                NewName = "B",
                Apply = true,
            },
            TestCt
        );

        response.Should().BeSuccessful();
        var text = ResponseText(response);
        var lines = ResultLines.Lines(text);
        lines[0]
            .Should()
            .Be($"applied: {fileCount} files, {fileCount} operations, Roslyn synchronized");
        lines
            .Where(line => line.StartsWith("change ", StringComparison.Ordinal))
            .Should()
            .NotBeEmpty()
            .And.AllSatisfy(line => files.Should().Contain(line["change ".Length..]));
        lines
            .Should()
            .Contain(line =>
                line.StartsWith(TruncatedPrefix, StringComparison.Ordinal)
                && line.EndsWith(" more operations not shown", StringComparison.Ordinal)
            );
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
    }
}
