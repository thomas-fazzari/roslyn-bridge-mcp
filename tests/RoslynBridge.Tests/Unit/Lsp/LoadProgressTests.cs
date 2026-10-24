// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Settings;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class LoadProgressTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-bridge-mcp-progress-")
        .FullName;

    private readonly BridgeOptions _options;

    public LoadProgressTests() =>
        _options = new BridgeOptions
        {
            WorkspaceRoot = _root,
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };

    [Fact(Timeout = 30_000)]
    public async Task StatusAndTheLoadingErrorReportTheProgressWhileTheWorkspaceLoadsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var load = NewCompletionSource();
        var launched = NewLaunch();
        await using var session = CreateSession(_root, Launcher(launched, load.Task), _options);
        using var tool = CreateTool(_root, session, _options);
        await session.StartLoadingAsync();
        var server = await launched.Task;

        await server.RequestClientAsync(
            LspMethods.WindowWorkDoneProgressCreate,
            new JsonObject { ["token"] = "load" },
            cancellationToken
        );
        await server.ReportProgressAsync(
            "load",
            Begin("Loading Workspace.slnx..."),
            cancellationToken
        );
        await server.ReportProgressAsync("load", Report(40), cancellationToken);
        var status = await tool.ExecuteAsync(StatusRequest(), cancellationToken);
        var loading = await tool.ExecuteAsync(SymbolQuery(), cancellationToken);

        ResponseText(status)
            .Should()
            .StartWith("running: true\nloading: true\nprogress: Loading Workspace.slnx 40%\n");
        loading.Should().BeError(ErrorCodes.Loading);
        ResponseText(loading).Should().Contain("Loading Workspace.slnx 40%");
    }

    [Fact(Timeout = 30_000)]
    public async Task NoProgressShowsOnceTheLoadHasEndedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var load = NewCompletionSource();
        var launched = NewLaunch();
        await using var session = CreateSession(_root, Launcher(launched, load.Task));
        using var tool = CreateTool(_root, session);
        await session.StartLoadingAsync();
        var server = await launched.Task;
        await server.ReportProgressAsync(
            "load",
            Begin("Loading Workspace.slnx"),
            cancellationToken
        );

        load.SetResult();
        await tool.ExecuteAsync(SymbolQuery(), cancellationToken);
        var status = await StatusAsync(tool);

        status.Should().Contain("loading: false").And.NotContain("progress:");
    }

    [Fact(Timeout = 30_000)]
    public async Task ProgressFollowsTheLatestOperationThatHasNotEndedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var launched = NewLaunch();
        await using var session = CreateSession(
            _root,
            Launcher(launched, NewCompletionSource().Task),
            _options
        );
        using var tool = CreateTool(_root, session, _options);
        await session.StartLoadingAsync();
        var server = await launched.Task;

        await server.ReportProgressAsync(
            "load",
            Begin("Loading projects", percentage: 10),
            cancellationToken
        );
        await server.ReportProgressAsync("restore", Begin("Restoring packages"), cancellationToken);
        var restoring = await StatusAsync(tool);
        await server.ReportProgressAsync("load", Report(60), cancellationToken);
        await server.ReportProgressAsync("restore", End(), cancellationToken);
        var loading = await StatusAsync(tool);
        await server.ReportProgressAsync("load", End(), cancellationToken);
        var ended = await StatusAsync(tool);

        restoring.Should().Contain("\nprogress: Restoring packages\n");
        loading.Should().Contain("\nprogress: Loading projects 60%\n");
        ended.Should().Contain("loading: true").And.NotContain("progress:");
    }

    [Fact(Timeout = 30_000)]
    public async Task ReloadClearsTheProgressOfThePreviousLoadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var launches = new List<TaskCompletionSource<FakeRoslynServer>>
        {
            NewLaunch(),
            NewLaunch(),
        };
        var count = 0;
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
            {
                server.ProjectLoad = NewCompletionSource().Task;
                launches[count++].SetResult(server);
            }),
            _options
        );
        using var tool = CreateTool(_root, session, _options);
        await session.StartLoadingAsync();
        var first = await launches[0].Task;
        await first.ReportProgressAsync("load", Begin("Loading Workspace.slnx"), cancellationToken);
        (await StatusAsync(tool)).Should().Contain("progress: Loading Workspace.slnx");

        await FluentActions.Awaiting(Reload).Should().ThrowAsync<WorkspaceLoadingException>();
        var status = await StatusAsync(tool);

        status.Should().Contain("loading: true").And.NotContain("progress:");
        return;

        Task Reload() => session.ReloadAsync(cancellationToken);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<FakeRoslynServer> NewLaunch() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static RoslynLauncher Launcher(
        TaskCompletionSource<FakeRoslynServer> launched,
        Task load
    ) =>
        FakeServer(server =>
        {
            server.ProjectLoad = load;
            launched.SetResult(server);
        });

    private static async Task<string> StatusAsync(LspTool tool) =>
        ResponseText(await tool.ExecuteAsync(StatusRequest(), TestCt));

    private static LspRequest StatusRequest() => new() { Action = LspAction.Status };

    private static LspRequest SymbolQuery() =>
        new() { Action = LspAction.Symbols, Query = "Greeter" };

    private static JsonObject Begin(string title, int? percentage = null) =>
        new()
        {
            ["kind"] = "begin",
            ["title"] = title,
            ["message"] = string.Empty,
            ["percentage"] = percentage,
        };

    private static JsonObject Report(int percentage) =>
        new() { ["kind"] = "report", ["percentage"] = percentage };

    private static JsonObject End() => new() { ["kind"] = "end", ["message"] = "Loaded" };
}
