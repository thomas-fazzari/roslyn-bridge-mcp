// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using RoslynBridge.Lsp;
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
        await using var session = CreateSession(
            _root,
            RoslynProcess.Start,
            new BridgeOptions { WorkspaceRoot = _root, ServerPath = server }
        );

        (await FluentActions.Awaiting(Start).Should().ThrowAsync<IOException>())
            .Which.Message.Should()
            .Contain("install .NET");
        return;

        Task Start() => session.EnsureStartedAsync(TestCt);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
