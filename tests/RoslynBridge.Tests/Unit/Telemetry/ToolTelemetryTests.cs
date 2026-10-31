// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using ModelContextProtocol.Protocol;
using RoslynBridge.Lsp;
using RoslynBridge.Telemetry;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Unit.Tools;
using RoslynBridge.Tools;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Telemetry;

[Collection(TelemetryCollection.Name)]
public sealed class ToolTelemetryTests : IAsyncDisposable
{
    private const int TimeoutMilliseconds = 30_000;

    private const string SourceFile = "Source.cs";
    private const string References = "references";
    private const string ToolSpan = $"execute_tool {NavigateTool.Name}";

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-tool-telemetry-")
        .FullName;
    private readonly TelemetryRecorder _recorder = new();
    private RoslynSession? _session;
    private LspTool? _tool;

    [Fact(Timeout = TimeoutMilliseconds)]
    public async Task RecordsASuccessfulCallAndNestsItsRoslynRequestsAsync()
    {
        var response = await CallReferencesAsync(
            failRoslyn: false,
            TestContext.Current.CancellationToken
        );

        response.Should().BeSuccessful();
        var size = _recorder
            .Measurements(BridgeTelemetry.ResultSize)
            .Should()
            .ContainSingle()
            .Subject;
        size.Value.Should().Be(ResponseText(response).Length);
        size.Tags.Should()
            .Contain(ToolTelemetry.ToolName, NavigateTool.Name)
            .And.Contain(ToolTelemetry.Action, References)
            .And.NotContainKey(BridgeTelemetry.ErrorType);
        var call = _recorder.Span(ToolSpan);
        var request = _recorder.Span(LspMethods.TextDocumentReferences);
        request.TraceId.Should().Be(call.TraceId);
        request.ParentSpanId.Should().Be(call.SpanId);
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    public async Task RecordsARejectedCallWithoutItsUnknownActionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await StartServerAsync(failRoslyn: false, cancellationToken);

        var response = await server.Client.CallToolAsync(
            NavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = "not-an-action",
                ["symbol"] = "Greeter",
            },
            cancellationToken: cancellationToken
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
        _recorder
            .Measurements(BridgeTelemetry.ToolDuration)
            .Should()
            .ContainSingle()
            .Which.Tags.Should()
            .Contain(BridgeTelemetry.ErrorType, ErrorCodes.InvalidRequest)
            .And.NotContainKey(ToolTelemetry.Action);
    }

    [Fact(Timeout = TimeoutMilliseconds)]
    public async Task RecordsAFailedRoslynRequestOnTheCallAndOnTheRequestAsync()
    {
        var response = await CallReferencesAsync(
            failRoslyn: true,
            TestContext.Current.CancellationToken
        );

        response.Should().BeError(ErrorCodes.Lsp);
        _recorder
            .Measurements(BridgeTelemetry.ToolDuration)
            .Should()
            .ContainSingle()
            .Which.Tags.Should()
            .Contain(BridgeTelemetry.ErrorType, ErrorCodes.Lsp);
        var request = _recorder.Span(LspMethods.TextDocumentReferences);
        request.Status.Should().Be(ActivityStatusCode.Error);
        request
            .GetTagItem(BridgeTelemetry.ErrorType)
            .Should()
            .Be(typeof(RemoteInvocationException).FullName);
    }

    public async ValueTask DisposeAsync()
    {
        _recorder.Dispose();
        _tool?.Dispose();
        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        Directory.Delete(_root, recursive: true);
    }

    private async Task<CallToolResult> CallReferencesAsync(
        bool failRoslyn,
        CancellationToken cancellationToken
    )
    {
        await WriteFileAsync(_root, SourceFile, "class A {}\n");
        await using var server = await StartServerAsync(failRoslyn, cancellationToken);
        return await server.Client.CallToolAsync(
            NavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = References,
                ["file"] = SourceFile,
                ["line"] = 1,
                ["character"] = 1,
            },
            cancellationToken: cancellationToken
        );
    }

    private Task<ToolServer> StartServerAsync(bool failRoslyn, CancellationToken cancellationToken)
    {
        _session = CreateSession(
            _root,
            FakeServer(server =>
            {
                if (failRoslyn)
                {
                    server.Handle = (method, _) =>
                        method is LspMethods.TextDocumentReferences
                            ? throw new LocalRpcException("Roslyn failed.")
                            : Task.FromResult<JsonNode?>(null);
                }
            })
        );
        _tool = CreateTool(_root, _session);
        return ToolServer.StartAsync(_tool, cancellationToken);
    }
}
