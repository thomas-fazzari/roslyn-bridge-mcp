// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class SessionLifecycleTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-bridge-mcp-session-")
        .FullName;

    [Fact]
    public async Task CrashDuringARequestReportsStderrAndRestartsOnTheNextCallAsync()
    {
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(_root, FakeServer(servers.Add));
        await session.EnsureStartedAsync(TestCt);
        await servers[0].WriteErrorAsync("Unhandled exception: out of memory", TestCt);
        servers[0].Handle = (_, _) =>
        {
            servers[0].Crash();
            return Task.FromResult<JsonNode?>(null);
        };

        var failure = await FluentActions.Awaiting(Request).Should().ThrowAsync<IOException>();
        failure.Which.Message.Should().Contain("out of memory");
        session.IsRunning.Should().BeFalse();

        await session.EnsureStartedAsync(TestCt);

        servers.Should().HaveCount(2);
        session.IsRunning.Should().BeTrue();
        return;

        Task Request() =>
            session.RequestAsync(
                LspMethods.WorkspaceSymbol,
                new JsonObject { ["query"] = "Greeter" },
                TestCt
            );
    }

    [Fact]
    public async Task SymbolicLinkCreatedAfterStartIsIgnoredAsync()
    {
        var target = Path.Combine(_root, "Target.cs");
        await File.WriteAllTextAsync(target, "class Target {}", TestCt);
        await using var session = CreateSession(_root, FakeServer());
        await session.EnsureStartedAsync(TestCt);
        var link = Path.Combine(_root, "Link.cs");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating a symbolic link needs privileges on this platform.");
        }

        // The watcher reports the link asynchronously, so queue it directly
        session.QueueFile(link);

        await session.SynchronizeAsync(TestCt);
        await session.SynchronizeAsync(TestCt);

        (await session.CopyDocumentVersionsAsync(TestCt)).Should().NotContainKey(link);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
