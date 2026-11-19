// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class SessionLifecycleTests : TempWorkspaceTests
{
    [Fact]
    public async Task CrashDuringARequestReportsStderrAndRestartsOnTheNextCallAsync()
    {
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();
        await servers[0].WriteErrorAsync("Unhandled exception: out of memory", TestCt);
        servers[0].Handle = (_, _) =>
        {
            servers[0].Crash();
            return Task.FromResult<JsonNode?>(null);
        };

        var failure = await session.RequestAsync(
            LspMethods.WorkspaceSymbol,
            new JsonObject { ["query"] = "Greeter" },
            TestCt
        );
        failure.ErrorCode().Should().Be(ErrorCodes.Io);
        (failure is ToolError error ? error.Message : null).Should().Contain("out of memory");
        session.IsRunning.Should().BeFalse();

        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();

        servers.Should().HaveCount(2);
        session.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task SymbolicLinkCreatedAfterStartIsIgnoredAsync()
    {
        var target = Path.Combine(Root, "Target.cs");
        await File.WriteAllTextAsync(target, "class Target {}", TestCt);
        await using var session = CreateSession(Root, FakeServer());
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();
        var link = Path.Combine(Root, "Link.cs");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs privileges on this platform.");
        }

        // The watcher reports the link asynchronously, so send it directly
        (await session.SynchronizeFilesAsync([link], TestCt)).ErrorCode().Should().BeNull();
        (await session.SynchronizeAsync(TestCt)).ErrorCode().Should().BeNull();

        (await session.CopyDocumentVersionsAsync(TestCt)).Should().NotContainKey(link);
    }
}
