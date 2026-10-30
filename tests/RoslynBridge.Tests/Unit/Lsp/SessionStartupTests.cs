// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Settings;
using RoslynBridge.Tests.Fakes;
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

    [Fact]
    public async Task OpensTheConfiguredSolutionAfterTheHandshakeAsync()
    {
        var solution = Path.Combine(_root, "App.slnx");

        var notifications = await StartAsync(new BridgeOptions { Solution = solution });

        notifications
            .Should()
            .ContainSingle(notification => notification.Method == LspMethods.SolutionOpen)
            .Which.Parameters!["solution"]!
            .GetValue<string>()
            .Should()
            .Be(new Uri(solution).AbsoluteUri);
        notifications
            .Should()
            .NotContain(notification => notification.Method == LspMethods.ProjectOpen);
    }

    [Fact]
    public async Task OpensTheConfiguredProjectsAfterTheHandshakeAsync()
    {
        var first = Path.Combine(_root, "A", "A.csproj");
        var second = Path.Combine(_root, "B", "B.csproj");

        var notifications = await StartAsync(new BridgeOptions { Projects = [first, second] });

        notifications
            .Should()
            .ContainSingle(notification => notification.Method == LspMethods.ProjectOpen)
            .Which.Parameters!["projects"]!
            .AsArray()
            .Select(project => project!.GetValue<string>())
            .Should()
            .Equal(new Uri(first).AbsoluteUri, new Uri(second).AbsoluteUri);
    }

    [Fact]
    public async Task LeavesTheChoiceToRoslynWithoutSettingsAsync()
    {
        var notifications = await StartAsync(new BridgeOptions());

        notifications.Should().BeEmpty();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<List<(string Method, JsonNode? Parameters)>> StartAsync(
        BridgeOptions options
    )
    {
        FakeRoslynServer? fake = null;
        await using var session = CreateSession(
            _root,
            FakeServer(server => fake = server),
            options with
            {
                WorkspaceRoot = _root,
            }
        );

        await session.EnsureStartedAsync(TestCt);
        // The answer to a request proves that the server has read the notifications before it
        await session.RequestAsync(LspMethods.WorkspaceSymbol, new JsonObject(), TestCt);

        return [.. fake!.Notifications];
    }
}
