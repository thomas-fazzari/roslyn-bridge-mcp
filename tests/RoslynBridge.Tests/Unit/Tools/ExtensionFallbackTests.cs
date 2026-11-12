// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using StreamJsonRpc;
using static RoslynBridge.Tests.Fakes.LspLocationJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class ExtensionFallbackTests : TempWorkspaceTests
{
    private const string FoundFile = "Found.cs";
    private const string ExtensionCallerFile = "ExtensionCaller.cs";
    private const string HierarchyCallerFile = "HierarchyCaller.cs";
    private const string CallerName = "Caller.Run()";
    private const int HandlerFailureCode = -32603;

    [Fact]
    public async Task FallsBackToTheReferencesRequestWhenTheExtensionAnswersAnEmptyResponseAsync()
    {
        await WriteFileAsync(Root, SourceFile, "class A {}\n");
        var found = await WriteFileAsync(Root, FoundFile, "class B {}\n");
        await using var session = CreateSession(
            Root,
            Answering(
                new(StringComparer.Ordinal)
                {
                    [LspMethods.ServerActivateExtension] = ActivatedHandlers(),
                    [LspMethods.TextDocumentDispatchExtensionMessage] = new JsonObject
                    {
                        ["response"] = string.Empty,
                    },
                    [LspMethods.TextDocumentReferences] = new JsonArray(Location(found)),
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(Request(LspAction.References), TestCt);

        ResponseText(response).Should().Be($"{FoundFile} 1:1");
    }

    [Theory]
    [InlineData(RoslynSession.ServerCancelledCode, 2)]
    [InlineData(HandlerFailureCode, 1)]
    public async Task DispatchFailureDisablesTheExtensionUnlessRoslynCancelledAsync(
        int errorCode,
        int expectedDispatches
    )
    {
        await WriteFileAsync(Root, SourceFile, "class A {}\n");
        var found = await WriteFileAsync(Root, FoundFile, "class B {}\n");
        var answers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [LspMethods.ServerActivateExtension] = ActivatedHandlers(),
            [LspMethods.TextDocumentReferences] = new JsonArray(Location(found)),
        };
        var dispatches = 0;
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                {
                    if (method is not LspMethods.TextDocumentDispatchExtensionMessage)
                    {
                        return Task.FromResult(answers.GetValueOrDefault(method));
                    }

                    Interlocked.Increment(ref dispatches);
                    throw new LocalRpcException("The extension failed.") { ErrorCode = errorCode };
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var first = await tool.ExecuteAsync(Request(LspAction.References), TestCt);
        var second = await tool.ExecuteAsync(Request(LspAction.References), TestCt);

        ResponseText(first).Should().Be($"{FoundFile} 1:1");
        ResponseText(second).Should().Be($"{FoundFile} 1:1");
        dispatches.Should().Be(expectedDispatches);
    }

    [Theory]
    [InlineData(true, ExtensionCallerFile)]
    [InlineData(false, HierarchyCallerFile)]
    public async Task CallersComeFromTheExtensionOnlyWhileItIsActiveAsync(
        bool activated,
        string expectedFile
    )
    {
        await WriteFileAsync(Root, SourceFile, "class A {}\n");
        var extensionCaller = await WriteFileAsync(Root, ExtensionCallerFile, "class B {}\n");
        var hierarchyCaller = await WriteFileAsync(Root, HierarchyCallerFile, "class C {}\n");
        var answers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [LspMethods.TextDocumentDispatchExtensionMessage] = new JsonObject
            {
                ["response"] = new JsonObject
                {
                    ["found"] = true,
                    ["calls"] = new JsonArray(IncomingCall(extensionCaller)),
                }.ToJsonString(),
            },
            [LspMethods.TextDocumentPrepareCallHierarchy] = new JsonArray(
                new JsonObject { ["name"] = CallerName, ["kind"] = LspSymbolKinds.Method }
            ),
            [LspMethods.CallHierarchyIncomingCalls] = new JsonArray(IncomingCall(hierarchyCaller)),
        };
        if (activated)
        {
            answers[LspMethods.ServerActivateExtension] = ActivatedHandlers();
        }

        await using var session = CreateSession(Root, Answering(answers));
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(Request(LspAction.Callers), TestCt);

        ResultLines.Locations(ResponseText(response)).Should().Equal(expectedFile);
    }

    [Fact]
    public async Task UnusedSymbolsFailWithoutTheWorkspaceHandlerAsync()
    {
        await WriteFileAsync(Root, SourceFile, "class A {}\n");
        await using var session = CreateSession(
            Root,
            Answering(
                new(StringComparer.Ordinal)
                {
                    [LspMethods.ServerActivateExtension] = ActivatedHandlers(),
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Symbols,
                File = SourceFile,
                Unused = true,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.Lsp);
    }

    /// <param name="answers">By LSP method. Other methods answer null.</param>
    private static RoslynLauncher Answering(Dictionary<string, JsonNode> answers) =>
        FakeServer(server =>
            server.Handle = (method, _) => Task.FromResult(answers.GetValueOrDefault(method))
        );

    private static LspRequest Request(LspAction action) =>
        new()
        {
            Action = action,
            File = SourceFile,
            Line = 1,
            Character = 1,
        };

    private static JsonObject IncomingCall(string path) =>
        new()
        {
            ["from"] = new JsonObject
            {
                ["name"] = CallerName,
                ["kind"] = LspSymbolKinds.Method,
                ["uri"] = new Uri(path).AbsoluteUri,
                ["selectionRange"] = Range(0, 6),
            },
            ["fromRanges"] = new JsonArray(),
        };

    private static JsonObject ActivatedHandlers() =>
        new()
        {
            ["documentMessageHandlers"] = new JsonArray(
                JsonValue.Create(RoslynSession.ReferencesMessage),
                JsonValue.Create(RoslynSession.CallersMessage)
            ),
        };
}
