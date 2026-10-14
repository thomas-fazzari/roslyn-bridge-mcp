// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Output;
using RoslynBridge.Requests;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LspApplicationTests
{
    [Fact]
    public void PreviewNearTheLimitStillFitsInTheResponse()
    {
        // Small files fill the budget almost exactly
        var files = new JsonArray();
        for (var index = 0; index < 4_000; index++)
        {
            files.Add(new JsonObject { ["path"] = "S.cs" });
        }
        var preview = new JsonObject
        {
            ["proposalId"] = "proposal",
            ["applied"] = false,
            ["fileCount"] = files.Count,
            ["files"] = files,
        };

        var limited = EditPreview.Limit(preview, ResultText.PreviewBudgetCharacters);
        var response = LspTool.Response(LspAction.Rename, limited);

        response.IsError.Should().BeFalse();
        ResponseText(response)
            .Should()
            .StartWith("proposal proposal: 4000 files, not applied")
            .And.Contain("files shown");
    }

    [Fact]
    public void LargeReadOnlyResultsStillReportTheLimit()
    {
        var locations = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["file"] = new string('x', ResultText.MaximumResponseCharacters),
                    ["positions"] = new JsonArray(new JsonArray(1, 1)),
                }
            ),
            ["total"] = 1,
        };

        var response = LspTool.Response(LspAction.References, locations);

        response.IsError.Should().BeTrue();
        ErrorCode(response).Should().Be(ErrorCodes.ResultTooLarge);
    }

    [Fact]
    public void LargeCommandResponsePreservesAppliedFiles()
    {
        var result = ApplicationResult(new JsonObject { ["path"] = "Source.cs" });
        result["commandExecuted"] = true;
        result["commandResult"] = new string('x', ResultText.MaximumResponseCharacters);

        var response = LspTool.Response(LspAction.Request, result, preserveApplication: true);

        response.IsError.Should().NotBeTrue();
        var text = ResponseText(response);
        text.Should()
            .StartWith("applied: 1 file")
            .And.Contain("change Source.cs")
            .And.Contain("result omitted");
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
    }

    [Fact]
    public void LargePathListsKeepExactPathsAndTotalCounts()
    {
        var path = new string('é', 2_000);
        var files = Enumerable
            .Range(0, 64)
            .Select(index => new JsonObject
            {
                ["path"] = path + index.ToString(CultureInfo.InvariantCulture),
            })
            .ToArray();

        var response = LspTool.Response(
            LspAction.Rename,
            ApplicationResult(files),
            preserveApplication: true
        );

        response.IsError.Should().NotBeTrue();
        var text = ResponseText(response);
        text.Should()
            .StartWith("applied: 64 files")
            .And.Contain($"change {path}0\n")
            .And.Contain("more operations not shown");
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
    }

    [Theory]
    [InlineData(true, ErrorCodes.Cancelled)]
    [InlineData(false, ErrorCodes.Timeout)]
    public void CancellationKeepsTheCompletedWriteReceipt(bool cancelled, string expectedCode)
    {
        var exception = new EditApplicationException(
            ApplicationResult(new JsonObject { ["path"] = "Source.cs" }),
            EditApplicationPhase.Synchronize,
            failedPath: null,
            failedOperation: null,
            new OperationCanceledException()
        );

        var response = LspTool.ApplicationFailure(LspAction.Rename, exception, cancelled);

        response.IsError.Should().BeTrue();
        ErrorCode(response).Should().Be(expectedCode);
        ResponseText(response)
            .Should()
            .Contain("applied: 1 file, 1 operation, Roslyn not synchronized");
    }

    private static JsonObject ApplicationResult(params JsonObject[] files) =>
        EditReceipt.Create(applied: true, files);
}
