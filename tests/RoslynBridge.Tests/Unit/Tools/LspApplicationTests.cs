// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Nodes;
using RoslynBridge.Editing;
using RoslynBridge.Tools;

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

        var limited = EditPreview.Limit(preview, LspTool.PreviewBudgetCharacters);
        var response = LspTool.Response(LspAction.Rename, limited);

        response.IsError.Should().BeFalse();
        var result = response.StructuredContent!.Value.GetProperty("result");
        result.GetProperty("proposalId").GetString().Should().Be("proposal");
        result.GetProperty("filesTruncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void LargeReadOnlyResultsStillReportTheLimit()
    {
        var response = LspTool.Response(
            LspAction.References,
            JsonValue.Create(new string('x', LspTool.MaximumResponseCharacters))
        );

        response.IsError.Should().BeTrue();
        response
            .StructuredContent!.Value.GetProperty("error")
            .GetProperty("code")
            .GetString()
            .Should()
            .Be(LspTool.ResultTooLargeErrorCode);
    }

    [Fact]
    public void LargeCommandResponsePreservesAppliedFiles()
    {
        var result = ApplicationResult(new JsonObject { ["path"] = "Source.cs" });
        result["commandResult"] = new string('x', LspTool.MaximumResponseCharacters);

        var response = LspTool.Response(LspAction.Request, result, preserveApplication: true);

        response.IsError.Should().NotBeTrue();
        var payload = response.StructuredContent!.Value;
        var application = payload.GetProperty("result");
        application.GetProperty("applied").GetBoolean().Should().BeTrue();
        application.GetProperty("fileCount").GetInt32().Should().Be(1);
        application
            .GetProperty("files")[0]
            .GetProperty("path")
            .GetString()
            .Should()
            .Be("Source.cs");
        application.GetProperty("commandResultTruncated").GetBoolean().Should().BeTrue();
        application.TryGetProperty("commandResult", out _).Should().BeFalse();
        payload.GetRawText().Length.Should().BeLessThanOrEqualTo(LspTool.MaximumResponseCharacters);
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
        var payload = response.StructuredContent!.Value;
        var application = payload.GetProperty("result");
        application.GetProperty("applied").GetBoolean().Should().BeTrue();
        application.GetProperty("fileCount").GetInt32().Should().Be(files.Length);
        application.GetProperty("filesTruncated").GetBoolean().Should().BeTrue();
        var included = application.GetProperty("files");
        included.GetArrayLength().Should().BeInRange(1, files.Length - 1);
        included[0].GetProperty("path").GetString().Should().Be(path + "0");
        payload.GetRawText().Length.Should().BeLessThanOrEqualTo(LspTool.MaximumResponseCharacters);
    }

    [Theory]
    [InlineData(true, LspTool.CancelledErrorCode)]
    [InlineData(false, LspTool.TimeoutErrorCode)]
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
        var payload = response.StructuredContent!.Value;
        payload.GetProperty("error").GetProperty("code").GetString().Should().Be(expectedCode);
        payload.GetProperty("result").GetProperty("applied").GetBoolean().Should().BeTrue();
        payload.GetProperty("result").GetProperty("synchronized").GetBoolean().Should().BeFalse();
    }

    private static JsonObject ApplicationResult(params JsonObject[] files) =>
        EditReceipt.Create(applied: true, files);
}
