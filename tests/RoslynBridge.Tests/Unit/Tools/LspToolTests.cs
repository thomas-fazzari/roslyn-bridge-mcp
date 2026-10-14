// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Symbols;
using RoslynBridge.Tests.Fakes;
using RoslynBridge.Tools;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LspToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-lsp-tool-").FullName;

    [Fact]
    public async Task BridgeBugIsNotReportedAsAnInvalidRequestAsync()
    {
        await using var session = CreateSession(
            (_, _) => throw new InvalidOperationException("Bridge invariant failed.")
        );
        using var tool = CreateTool(session);

        await FluentActions.Awaiting(Execute).Should().ThrowAsync<InvalidOperationException>();
        return;

        Task Execute() => tool.ExecuteAsync(new LspRequest { Action = LspAction.Symbols }, TestCt);
    }

    [Fact]
    public async Task ErrorTextCarriesTheStructuredErrorAsync()
    {
        await using var session = CreateSession(FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, Limit = 0 },
            TestCt
        );

        var text = response.Content.OfType<TextContentBlock>().Single().Text;
        JsonNode.Parse(text)!["error"]!["code"]!
            .GetValue<string>()
            .Should()
            .Be(LspTool.InvalidRequestErrorCode);
    }

    [Theory]
    [InlineData(LspMethods.Initialize)]
    [InlineData(LspMethods.TextDocumentDidOpen)]
    [InlineData(LspMethods.WorkspaceDidChangeWatchedFiles)]
    [InlineData(LspMethods.WorkspaceApplyEdit)]
    public async Task RawRequestRefusesMethodsTheBridgeOwnsAsync(string method)
    {
        await using var session = CreateSession(FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = method,
                Apply = true,
            },
            TestCt
        );

        ErrorCode(response).Should().Be(LspTool.InvalidRequestErrorCode);
    }

    [Theory]
    [InlineData(LspMethods.WorkspaceExecuteCommand)]
    [InlineData("custom/unlistedMethod")]
    public async Task RawRequestOutsideTheReadOnlyMethodsRequiresApplyAsync(string method)
    {
        await using var session = CreateSession(FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = method,
                Parameters = new JsonObject { ["command"] = "test.command" },
            },
            TestCt
        );

        ErrorCode(response).Should().Be(LspTool.InvalidRequestErrorCode);
    }

    [Fact]
    public async Task CommandFailureAfterAWriteReportsTheAppliedFilesAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        await using var session = CreateSession(
            FakeServer(server =>
                server.Handle = async (_, _) =>
                {
                    await server.RequestClientAsync(
                        LspMethods.WorkspaceApplyEdit,
                        new JsonObject { ["edit"] = Changes(source, TextEdit(0, 6, 0, 7, "B")) },
                        TestCt
                    );
                    throw new LocalRpcException("The command failed after its edit.");
                }
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = LspMethods.WorkspaceExecuteCommand,
                Parameters = new JsonObject { ["command"] = "test.command" },
                Apply = true,
            },
            TestCt
        );

        ErrorCode(response).Should().Be(LspTool.LspErrorCode);
        var payload = response.StructuredContent!.Value;
        payload
            .GetProperty("error")
            .GetProperty("phase")
            .Deserialize(BridgeJsonContext.Default.EditApplicationPhase)
            .Should()
            .Be(EditApplicationPhase.Command);
        var result = payload.GetProperty("result");
        result.GetProperty("synchronized").GetBoolean().Should().BeFalse();
        result.GetProperty("fileCount").GetInt32().Should().Be(1);
        result.GetProperty("files")[0].GetProperty("path").GetString().Should().Be("Source.cs");
        await AssertFileTextAsync(source, "class B {}");
    }

    [Fact]
    public async Task CallReportsLoadingWhenTheWorkspaceLoadsPastTheStartupTimeoutAsync()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new BridgeOptions
        {
            WorkspaceRoot = _root,
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };
        await using var session = CreateSession(
            FakeServer(server => server.ProjectLoad = load.Task),
            options
        );
        using var tool = CreateTool(session, options);

        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);

        ErrorCode(response).Should().Be(LspTool.LoadingErrorCode);
        session.IsLoading.Should().BeTrue();
    }

    [Fact]
    public async Task CanceledCallLeavesTheLoadForTheNextCallAsync()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        await using var session = CreateSession(
            FakeServer(server =>
            {
                launches++;
                server.ProjectLoad = load.Task;
                launched.SetResult();
            })
        );
        using var tool = CreateTool(session);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCt);

        var canceled = tool.ExecuteAsync(SymbolQuery(), cancellation.Token);
        await launched.Task;
        await cancellation.CancelAsync();
        await FluentActions.Awaiting(Canceled).Should().ThrowAsync<OperationCanceledException>();
        load.SetResult();
        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);

        response.IsError.Should().NotBe(true);
        launches.Should().Be(1);
        return;

        Task Canceled() => canceled;
    }

    [Fact(Timeout = 30_000)]
    public async Task StatusAnswersWhileAnotherCallWaitsForTheLoadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = CreateSession(
            FakeServer(server =>
            {
                server.ProjectLoad = load.Task;
                launched.SetResult();
            })
        );
        using var tool = CreateTool(session);

        var waiting = tool.ExecuteAsync(SymbolQuery(), cancellationToken);
        await launched.Task;
        var status = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Status },
            cancellationToken
        );
        load.SetResult();
        await waiting;

        status
            .StructuredContent!.Value.GetProperty("result")
            .GetProperty("loading")
            .GetBoolean()
            .Should()
            .BeTrue();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static LspRequest SymbolQuery() =>
        new() { Action = LspAction.Symbols, Query = "Greeter" };

    private static RoslynLauncher FakeServer(Action<FakeRoslynServer>? configure = null) =>
        (_, _) =>
        {
            var server = new FakeRoslynServer();
            configure?.Invoke(server);
            return server;
        };

    private RoslynSession CreateSession(RoslynLauncher launcher, BridgeOptions? options = null) =>
        new(
            options ?? new BridgeOptions { WorkspaceRoot = _root },
            new WorkspacePaths(_root),
            launcher,
            NullLogger<RoslynSession>.Instance
        );

    private LspTool CreateTool(RoslynSession session, BridgeOptions? options = null)
    {
        options ??= new BridgeOptions { WorkspaceRoot = _root };
        var paths = new WorkspacePaths(_root);
        var queries = new LspQueries(session, paths, new SymbolResolver(session, paths));
        return new LspTool(
            options,
            session,
            queries,
            new LspChanges(session, paths, new WorkspaceEditService(paths), queries),
            NullLogger<LspTool>.Instance
        );
    }

    private static string? ErrorCode(CallToolResult response) =>
        response.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString();
}
