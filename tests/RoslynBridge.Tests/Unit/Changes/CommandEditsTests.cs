// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Results;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Changes;

public sealed class CommandEditsTests : TempWorkspaceTests
{
    private const int TEST_TIMEOUT_MILLISECONDS = 30_000;

    [Fact]
    public async Task CaptureFailureAfterApplyAcknowledgesWrittenFilesAndClosesCallbacksAsync()
    {
        var path = await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var captureCalls = 0;
        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            _ =>
            {
                captureCalls++;
                return Task.FromResult<Result<WorkspaceSnapshot>>(ToolError.Io("capture failed"));
            },
            TestCt
        );

        var acknowledgement = await commandEdits.ApplyAsync(
            Edit(path, TextEdit(0, 6, 0, 7, "B")),
            TestCt
        );

        acknowledgement.Applied.Should().BeTrue();
        captureCalls.Should().Be(1);
        commandEdits
            .Failure.Should()
            .BeOfType<EditFailure>()
            .Which.Phase.Should()
            .Be(EditApplicationPhase.Synchronize);
        commandEdits.Result.FileCount.Should().Be(1);
        commandEdits.Result.Operations[0].Path.Should().Be(SOURCE_FILE);
        await AssertFileTextAsync(path, "class B {}");

        var rejected = await commandEdits.ApplyAsync(Edit(path, TextEdit(0, 6, 0, 7, "C")), TestCt);

        rejected.Applied.Should().BeFalse();
        await AssertFileTextAsync(path, "class B {}");
        commandEdits.Result.Operations.Count.Should().Be(1);
    }

    [Fact]
    public async Task SecondCallbackFailureRetainsTheFirstReceiptAsync()
    {
        var path = await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            _ => Task.FromResult<Result<WorkspaceSnapshot>>(Snapshot(Root)),
            TestCt
        );

        var first = await commandEdits.ApplyAsync(Edit(path, TextEdit(0, 6, 0, 7, "B")), TestCt);
        var second = await commandEdits.ApplyAsync(
            Edit(Path.Combine(Root, "Missing.cs"), TextEdit(0, 0, 0, 0, "class Missing {}")),
            TestCt
        );

        first.Applied.Should().BeTrue();
        second.Applied.Should().BeFalse();
        commandEdits.Failure.Should().NotBeNull();
        commandEdits.Result.FileCount.Should().Be(1);
        commandEdits.Result.Operations.Count.Should().Be(1);
        await AssertFileTextAsync(path, "class B {}");
        File.Exists(Path.Combine(Root, "Missing.cs")).Should().BeFalse();
    }

    [Fact(Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ConcurrentCallbacksApplyAndCaptureInOrderAsync()
    {
        // Tests with a timeout must reference the xUnit token directly
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstPath = await WriteFileAsync(Root, "First.cs", "class A {}");
        var secondPath = await WriteFileAsync(Root, "Second.cs", "class B {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var firstCaptureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var captureCalls = 0;

        async Task<Result<WorkspaceSnapshot>> CaptureAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref captureCalls) == 1)
            {
                firstCaptureStarted.TrySetResult();
                await releaseFirstCapture.Task.WaitAsync(token);
            }

            return Snapshot(Root);
        }

        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            CaptureAsync,
            cancellationToken
        );
        var first = commandEdits.ApplyAsync(
            Edit(firstPath, TextEdit(0, 6, 0, 7, "Changed")),
            cancellationToken
        );
        await firstCaptureStarted.Task.WaitAsync(cancellationToken);
        var second = commandEdits.ApplyAsync(
            Edit(secondPath, TextEdit(0, 6, 0, 7, "Changed")),
            cancellationToken
        );

        second.IsCompleted.Should().BeFalse();
        releaseFirstCapture.TrySetResult();

        (await first).Applied.Should().BeTrue();
        (await second).Applied.Should().BeTrue();
        captureCalls.Should().Be(2);
        commandEdits.Result.Operations.Count.Should().Be(2);
        await AssertFileTextAsync(firstPath, "class Changed {}");
        await AssertFileTextAsync(secondPath, "class Changed {}");
    }

    [Fact(Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task StopDuringCapturePreservesTheWriteAndRejectsLaterCallbacksAsync()
    {
        // Tests with a timeout must reference the xUnit token directly
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = await WriteFileAsync(Root, SOURCE_FILE, "class A {}");
        var service = CreateService();
        var snapshot = Snapshot(Root);
        var captureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        async Task<Result<WorkspaceSnapshot>> CaptureAsync(CancellationToken token)
        {
            captureStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Snapshot(Root);
        }

        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            CaptureAsync,
            cancellationToken
        );
        var pending = commandEdits.ApplyAsync(
            Edit(path, TextEdit(0, 6, 0, 7, "B")),
            cancellationToken
        );
        await captureStarted.Task.WaitAsync(cancellationToken);

        await commandEdits.StopAsync();

        // Stop waits for the callback's work, not for its task, which completes just after
        commandEdits
            .Failure.Should()
            .BeOfType<EditFailure>()
            .Which.Code.Should()
            .Be(ErrorCodes.CANCELLED);
        commandEdits.Result.Applied.Should().BeTrue();
        commandEdits.Result.FileCount.Should().Be(1);
        (await pending).Applied.Should().BeTrue();
        var rejected = await commandEdits.ApplyAsync(
            Edit(path, TextEdit(0, 6, 0, 7, "C")),
            cancellationToken
        );
        rejected.Applied.Should().BeFalse();
        await AssertFileTextAsync(path, "class B {}");
    }

    private WorkspaceEditService CreateService() => new(new WorkspacePaths(Root));

    private static Result<WorkspaceEdit> Edit(string path, params JsonObject[] edits) =>
        WorkspaceEdit.Parse(WorkspaceChanges(path, edits));
}
