// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using ModelContextProtocol.Protocol;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Fakes.LspSymbolJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class SymbolNotFoundTests : TempWorkspaceTests
{
    [Fact]
    public async Task KnownContainerWithoutTheMemberSaysSoWhenAnotherTypeHasItAsync()
    {
        var manager = await WriteFileAsync(
            Root,
            "ISessionManager.cs",
            "interface ISessionManager {}\n"
        );
        var controller = await WriteFileAsync(Root, "Controller.cs", "class Controller {}\n");
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["ReportPlaybackStart"] = [MethodSymbol("ReportPlaybackStart", controller)],
                ["ISessionManager"] = [WorkspaceSymbol("ISessionManager", manager)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["ISessionManager.cs"] =
                [
                    DocumentSymbol("ISessionManager", DocumentMethod("OnPlaybackStart()")),
                ],
                ["Controller.cs"] =
                [
                    DocumentSymbol("Controller", DocumentMethod("ReportPlaybackStart()")),
                ],
            }
        );

        var response = await CallAsync(server, "ISessionManager.ReportPlaybackStart");

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response).Should().Contain("'ISessionManager' has no member");
        CandidateNames(response)
            .Should()
            .Equal("ISessionManager.OnPlaybackStart()", "Controller.ReportPlaybackStart()");
    }

    [Fact]
    public async Task KnownContainerWithTheMemberUnderOtherParametersListsItsOverloadFirstAsync()
    {
        var calculator = await WriteFileAsync(Root, "Calculator.cs", "class Calculator {}\n");
        var other = await WriteFileAsync(Root, "Other.cs", "class Other {}\n");
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Add"] = [MethodSymbol("Add", other), MethodSymbol("Add", calculator)],
                ["Calculator"] = [WorkspaceSymbol("Calculator", calculator)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["Calculator.cs"] = [DocumentSymbol("Calculator", DocumentMethod("Add()"))],
                ["Other.cs"] = [DocumentSymbol("Other", DocumentMethod("Add()"))],
            }
        );

        var response = await CallAsync(server, "Calculator.Add(int)");

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response).Should().NotContain("has no member");
        CandidateNames(response).Should().Equal("Calculator.Add()", "Other.Add()");
    }

    private async Task<CallToolResult> CallAsync(RoslynLauncher server, string symbol)
    {
        await using var session = CreateSession(Root, server);
        using var tool = CreateTool(Root, session);
        return await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.References, Symbol = symbol },
            TestCt
        );
    }

    private static string[] CandidateNames(CallToolResult response) =>
        [
            .. ResultLines
                .Lines(ResponseText(response))
                .Skip(1)
                .Select(line => line.Split(' ', 3)[2]),
        ];
}
