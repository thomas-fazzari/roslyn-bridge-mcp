// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class ToolArgumentTests : IAsyncDisposable
{
    private const string SourceFile = "Source.cs";

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-tool-arguments-")
        .FullName;
    private readonly RoslynSession _session;
    private readonly LspTool _tool;
    private int _launches;

    public ToolArgumentTests()
    {
        _session = CreateSession(_root, FakeServer(_ => _launches++));
        _tool = CreateTool(_root, _session);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("limt", "limit")]
    [InlineData("fine", null)]
    public async Task UnknownArgumentIsRejectedWithoutRunningTheToolAsync(
        string argument,
        string? suggestion
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await ToolServer.StartAsync(_tool, cancellationToken);

        var response = await server.Client.CallToolAsync(
            NavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["action"] = "definition",
                ["symbol"] = "Greeter",
                [argument] = 3,
            },
            cancellationToken: cancellationToken
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
        ResponseText(response).Should().Contain($"'{argument}'");
        if (suggestion is null)
        {
            ResponseText(response).Should().NotContain("Did you mean");
        }
        else
        {
            ResponseText(response).Should().Contain($"Did you mean '{suggestion}'?");
        }

        _launches.Should().Be(0);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingRequiredArgumentIsRejectedWithoutRunningTheToolAsync(
        bool sendOtherArguments
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await ToolServer.StartAsync(_tool, cancellationToken);

        var response = await server.Client.CallToolAsync(
            NavigateTool.Name,
            sendOtherArguments
                ? new Dictionary<string, object?>(StringComparer.Ordinal) { ["symbol"] = "Greeter" }
                : null,
            cancellationToken: cancellationToken
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
        ResponseText(response).Should().Contain("'action'");
        _launches.Should().Be(0);
    }

    [Fact(Timeout = 30_000)]
    public async Task QueriesSentAsAJsonStringAreParsedAsAnArrayAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteFileAsync(_root, SourceFile, "class A {}\n");
        await using var server = await ToolServer.StartAsync(_tool, cancellationToken);

        var response = await server.Client.CallToolAsync(
            BulkNavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["queries"] =
                    $$"""[{"action":"references","file":"{{SourceFile}}","line":1,"character":1}]""",
            },
            cancellationToken: cancellationToken
        );

        response.Should().BeSuccessful();
        ResponseText(response).Should().Be($"## 1 references {SourceFile}:1:1: none");
        _launches.Should().Be(1);
    }

    [Theory(Timeout = 30_000)]
    [InlineData("not json")]
    [InlineData("""{"action":"references"}""")]
    public async Task QueriesSentAsAStringThatIsNotAnArrayAreRejectedAsync(string queries)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await ToolServer.StartAsync(_tool, cancellationToken);

        var response = await server.Client.CallToolAsync(
            BulkNavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["queries"] = queries },
            cancellationToken: cancellationToken
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
        ResponseText(response).Should().Contain("'queries'");
        _launches.Should().Be(0);
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownKeyInABulkQueryIsRejectedWithoutRunningTheToolAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteFileAsync(_root, SourceFile, "class A {}\n");
        await using var server = await ToolServer.StartAsync(_tool, cancellationToken);

        var response = await server.Client.CallToolAsync(
            BulkNavigateTool.Name,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["queries"] = new JsonArray(
                    new JsonObject
                    {
                        ["action"] = "references",
                        ["file"] = SourceFile,
                        ["bogus"] = 1,
                    }
                ),
            },
            cancellationToken: cancellationToken
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
        ResponseText(response).Should().Contain("'bogus'");
        _launches.Should().Be(0);
    }

    public async ValueTask DisposeAsync()
    {
        _tool.Dispose();
        await _session.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
