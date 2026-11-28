// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using ModelContextProtocol.Protocol;
using RoslynBridge.Features.Navigate;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Telemetry;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Unit.Tools;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Telemetry;

[Collection(TelemetryCollection.NAME)]
public sealed class ToolTelemetryTests : IAsyncDisposable
{
    private const int TIMEOUT_MILLISECONDS = 30_000;

    private const string SOURCE_FILE = "Source.cs";
    private const string REFERENCES = "references";
    private static readonly string _toolSpan = $"execute_tool {NavigateTool.Name}";

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-tool-telemetry-")
        .FullName;
    private readonly TelemetryRecorder _recorder = new();
    private RoslynSession? _session;

    [Fact(Timeout = TIMEOUT_MILLISECONDS)]
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
            .Contain(ToolTelemetry.TOOL_NAME, NavigateTool.Name)
            .And.Contain(ToolTelemetry.ACTION, REFERENCES)
            .And.NotContainKey(BridgeTelemetry.ERROR_TYPE);
        var call = _recorder.Span(_toolSpan);
        var request = _recorder.Span(LspMethods.TEXT_DOCUMENT_REFERENCES);
        request.TraceId.Should().Be(call.TraceId);
        request.ParentSpanId.Should().Be(call.SpanId);
    }

    [Fact(Timeout = TIMEOUT_MILLISECONDS)]
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

        response.Should().BeError(ErrorCodes.INVALID_REQUEST);
        _recorder
            .Measurements(BridgeTelemetry.ToolDuration)
            .Should()
            .ContainSingle()
            .Which.Tags.Should()
            .Contain(BridgeTelemetry.ERROR_TYPE, ErrorCodes.INVALID_REQUEST)
            .And.NotContainKey(ToolTelemetry.ACTION);
    }

    [Fact(Timeout = TIMEOUT_MILLISECONDS)]
    public async Task RecordsAFailedRoslynRequestOnTheCallAndOnTheRequestAsync()
    {
        var response = await CallReferencesAsync(
            failRoslyn: true,
            TestContext.Current.CancellationToken
        );

        response.Should().BeError(ErrorCodes.LSP);
        _recorder
            .Measurements(BridgeTelemetry.ToolDuration)
            .Should()
            .ContainSingle()
            .Which.Tags.Should()
            .Contain(BridgeTelemetry.ERROR_TYPE, ErrorCodes.LSP);
        var request = _recorder.Span(LspMethods.TEXT_DOCUMENT_REFERENCES);
        request.Status.Should().Be(ActivityStatusCode.Error);
        request.GetTagItem(BridgeTelemetry.ERROR_TYPE).Should().Be(ErrorCodes.LSP);
    }

    public async ValueTask DisposeAsync()
    {
        _recorder.Dispose();
        if (_session is not null)
            await _session.DisposeAsync();

        Directory.Delete(_root, recursive: true);
    }

    private async Task<CallToolResult> CallReferencesAsync(
        bool failRoslyn,
        CancellationToken cancellationToken
    )
    {
        await WriteFileAsync(_root, SOURCE_FILE, "class A {}\n");
        await using var server = await StartServerAsync(failRoslyn, cancellationToken);
        return await server.Client.CallToolAsync(
            NavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = REFERENCES,
                ["file"] = SOURCE_FILE,
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
                        method is LspMethods.TEXT_DOCUMENT_REFERENCES
                            ? throw new LocalRpcException("Roslyn failed.")
                            : Task.FromResult<JsonNode?>(null);
                }
            })
        );
        return ToolServer.StartAsync(_root, _session, cancellationToken);
    }
}
