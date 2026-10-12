// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using RoslynBridge.Editing;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class CommandEditsTests : IDisposable
{
    private const int TestTimeoutMilliseconds = 30_000;

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-command-edits-")
        .FullName;

    [Fact]
    public async Task CaptureFailureAfterApplyAcknowledgesWrittenFilesAndClosesCallbacksAsync()
    {
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        var captureCalls = 0;
        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            _ =>
            {
                captureCalls++;
                throw new IOException("capture failed");
            },
            TestCt
        );

        var acknowledgement = await commandEdits.ApplyAsync(
            Changes(path, TextEdit(0, 6, 0, 7, "B")),
            TestCt
        );

        acknowledgement["applied"]!.GetValue<bool>().Should().BeTrue();
        captureCalls.Should().Be(1);
        commandEdits.Failure.Should().BeOfType<EditApplicationException>();
        ((EditApplicationException)commandEdits.Failure!)
            .Phase.Should()
            .Be(EditApplicationPhase.Synchronize);
        commandEdits.Result["fileCount"]!.GetValue<int>().Should().Be(1);
        commandEdits.Result["files"]![0]!["path"]!.GetValue<string>().Should().Be("Source.cs");
        await AssertFileTextAsync(path, "class B {}");

        var rejected = await commandEdits.ApplyAsync(
            Changes(path, TextEdit(0, 6, 0, 7, "C")),
            TestCt
        );

        rejected["applied"]!.GetValue<bool>().Should().BeFalse();
        await AssertFileTextAsync(path, "class B {}");
        commandEdits.Result["operationCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public async Task SecondCallbackFailureRetainsTheFirstReceiptAsync()
    {
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(TestCt);
        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            token => service.CaptureAsync(token),
            TestCt
        );

        var first = await commandEdits.ApplyAsync(Changes(path, TextEdit(0, 6, 0, 7, "B")), TestCt);
        var second = await commandEdits.ApplyAsync(
            Changes(Path.Combine(_root, "Missing.cs"), TextEdit(0, 0, 0, 0, "class Missing {}")),
            TestCt
        );

        first["applied"]!.GetValue<bool>().Should().BeTrue();
        second["applied"]!.GetValue<bool>().Should().BeFalse();
        commandEdits.Failure.Should().NotBeNull();
        commandEdits.Result["fileCount"]!.GetValue<int>().Should().Be(1);
        commandEdits.Result["operationCount"]!.GetValue<int>().Should().Be(1);
        await AssertFileTextAsync(path, "class B {}");
        File.Exists(Path.Combine(_root, "Missing.cs")).Should().BeFalse();
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task ConcurrentCallbacksApplyAndCaptureInOrderAsync()
    {
        // Tests with a timeout must reference the xUnit token directly
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstPath = await WriteFileAsync(_root, "First.cs", "class A {}");
        var secondPath = await WriteFileAsync(_root, "Second.cs", "class B {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(cancellationToken);
        var firstCaptureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var releaseFirstCapture = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var captureCalls = 0;

        async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref captureCalls) == 1)
            {
                firstCaptureStarted.TrySetResult();
                await releaseFirstCapture.Task.WaitAsync(token);
            }

            return await service.CaptureAsync(token);
        }

        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            CaptureAsync,
            cancellationToken
        );
        var first = commandEdits.ApplyAsync(
            Changes(firstPath, TextEdit(0, 6, 0, 7, "Changed")),
            cancellationToken
        );
        await firstCaptureStarted.Task.WaitAsync(cancellationToken);
        var second = commandEdits.ApplyAsync(
            Changes(secondPath, TextEdit(0, 6, 0, 7, "Changed")),
            cancellationToken
        );

        second.IsCompleted.Should().BeFalse();
        releaseFirstCapture.TrySetResult();

        (await first)["applied"]!.GetValue<bool>().Should().BeTrue();
        (await second)["applied"]!.GetValue<bool>().Should().BeTrue();
        captureCalls.Should().Be(2);
        commandEdits.Result["operationCount"]!.GetValue<int>().Should().Be(2);
        await AssertFileTextAsync(firstPath, "class Changed {}");
        await AssertFileTextAsync(secondPath, "class Changed {}");
    }

    [Fact(Timeout = TestTimeoutMilliseconds)]
    public async Task StopDuringCapturePreservesTheWriteAndRejectsLaterCallbacksAsync()
    {
        // Tests with a timeout must reference the xUnit token directly
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = await WriteFileAsync(_root, "Source.cs", "class A {}");
        var service = CreateService();
        var snapshot = await service.CaptureAsync(cancellationToken);
        var captureStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        async Task<WorkspaceSnapshot> CaptureAsync(CancellationToken token)
        {
            captureStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return await service.CaptureAsync(token);
        }

        await using var commandEdits = new CommandEdits(
            service,
            snapshot,
            CaptureAsync,
            cancellationToken
        );
        var pending = commandEdits.ApplyAsync(
            Changes(path, TextEdit(0, 6, 0, 7, "B")),
            cancellationToken
        );
        await captureStarted.Task.WaitAsync(cancellationToken);

        await commandEdits.StopAsync();

        pending.IsCompleted.Should().BeTrue();
        (await pending)["applied"]!.GetValue<bool>().Should().BeTrue();
        commandEdits
            .Failure.Should()
            .BeOfType<EditApplicationException>()
            .Which.InnerException.Should()
            .BeAssignableTo<OperationCanceledException>();
        commandEdits.Result["applied"]!.GetValue<bool>().Should().BeTrue();
        commandEdits.Result["fileCount"]!.GetValue<int>().Should().Be(1);
        var rejected = await commandEdits.ApplyAsync(
            Changes(path, TextEdit(0, 6, 0, 7, "C")),
            cancellationToken
        );
        rejected["applied"]!.GetValue<bool>().Should().BeFalse();
        await AssertFileTextAsync(path, "class B {}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WorkspaceEditService CreateService() => new(new WorkspacePaths(_root));
}
