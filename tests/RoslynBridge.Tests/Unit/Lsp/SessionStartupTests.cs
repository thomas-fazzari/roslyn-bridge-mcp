// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using Microsoft.Extensions.Logging.Abstractions;
using RoslynBridge.Lsp;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class SessionStartupTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-bridge-mcp-startup-")
        .FullName;

    [Fact]
    public async Task ReportsStderrWhenTheServerExitsDuringStartupAsync()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake server is a shell script.");
        var server = Path.Combine(_root, "server.sh");
        await File.WriteAllTextAsync(
            server,
            "#!/bin/sh\necho 'You must install .NET to run this application.' >&2\nexit 1\n",
            TestCt
        );
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        var paths = new WorkspacePaths(_root);
        await using var session = new RoslynSession(
            new BridgeOptions { WorkspaceRoot = _root, ServerPath = server },
            paths,
            RoslynProcess.Start,
            NullLogger<RoslynSession>.Instance
        );

        (await FluentActions.Awaiting(Start).Should().ThrowAsync<IOException>())
            .Which.Message.Should()
            .Contain("install .NET");
        return;

        Task Start() => session.EnsureStartedAsync(TestCt);
    }

    [Fact]
    public async Task ReportsAMissingServerExecutableAsync()
    {
        var paths = new WorkspacePaths(_root);
        await using var session = new RoslynSession(
            new BridgeOptions
            {
                WorkspaceRoot = _root,
                ServerPath = Path.Combine(_root, "missing-server"),
            },
            paths,
            RoslynProcess.Start,
            NullLogger<RoslynSession>.Instance
        );

        var exception = (
            await FluentActions.Awaiting(Start).Should().ThrowAsync<Exception>()
        ).Which;
        LspTool.FailureCode(exception).Should().Be(LspTool.IoErrorCode);
        return;

        Task Start() => session.EnsureStartedAsync(TestCt);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
