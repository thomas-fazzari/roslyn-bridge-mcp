// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using RoslynBridge.Editing;
using RoslynBridge.Output;
using RoslynBridge.Results;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LspApplicationTests
{
    [Fact]
    public void LargePreviewDropsTrailingFilesAndTheCommandToFit()
    {
        // Short lines cost more as text than as JSON, so the budget must hold for rendered text
        var change = new TextChange(
            new SourcePosition(1, 1),
            string.Concat(Enumerable.Repeat("<\n", 50)),
            new SourcePosition(1, 1),
            string.Concat(Enumerable.Repeat(">\n", 50))
        );
        var files = Enumerable
            .Repeat(
                new FilePreview(
                    new FileOperation("S.cs", FileOperation.Change),
                    [change],
                    Truncated: false
                ),
                1_000
            )
            .ToList();
        var preview = new ProposalPreview(
            "proposal",
            files,
            new string('x', ResultText.MaximumResponseCharacters)
        );

        var response = LspTool.Response(preview);

        response.IsError.Should().BeFalse();
        var text = ResponseText(response);
        text.Should()
            .StartWith("proposal proposal: 1000 files, not applied")
            .And.Contain("command omitted")
            .And.Contain("files shown");
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
    }

    [Fact]
    public void LargeReadOnlyResultsStillReportTheLimit()
    {
        var locations = new Locations(
            [
                new FileLocations(
                    new string('x', ResultText.MaximumResponseCharacters),
                    [new SourcePosition(1, 1)]
                ),
            ],
            Total: 1,
            NextOffset: null,
            Generated: 0
        );

        var response = LspTool.Response(locations);

        response.IsError.Should().BeTrue();
        ErrorCode(response).Should().Be(ErrorCodes.ResultTooLarge);
    }

    [Fact]
    public void LargeCommandResponsePreservesAppliedFiles()
    {
        var receipt = Receipt("Source.cs") with
        {
            CommandExecuted = true,
            CommandResult = new string('x', ResultText.MaximumResponseCharacters),
        };

        var response = LspTool.Response(receipt);

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
        var receipt = Receipt([
            .. Enumerable
                .Range(0, 64)
                .Select(index => path + index.ToString(CultureInfo.InvariantCulture)),
        ]);

        var response = LspTool.Response(receipt);

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
            Receipt("Source.cs"),
            EditApplicationPhase.Synchronize,
            failedPath: null,
            failedOperation: null,
            new OperationCanceledException()
        );

        var response = LspTool.ApplicationFailure(exception, cancelled);

        response.IsError.Should().BeTrue();
        ErrorCode(response).Should().Be(expectedCode);
        ResponseText(response)
            .Should()
            .Contain("applied: 1 file, 1 operation, Roslyn not synchronized");
    }

    private static EditReceipt Receipt(params string[] paths) =>
        new(
            Applied: true,
            [.. paths.Select(path => new FileOperation(path, FileOperation.Change))]
        );
}
