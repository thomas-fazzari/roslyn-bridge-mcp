// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Tests.Assertions;
using StreamJsonRpc;
using static RoslynBridge.Tests.Fakes.LspLocationJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class ExtensionFallbackTests : TempWorkspaceTests
{
    private const string FOUND_FILE = "Found.cs";
    private const string EXTENSION_CALLER_FILE = "ExtensionCaller.cs";
    private const string HIERARCHY_CALLER_FILE = "HierarchyCaller.cs";
    private const string CALLER_NAME = "Caller.Run()";
    private const int HANDLER_FAILURE_CODE = -32603;

    [Theory]
    [InlineData("")]
    [InlineData("""{"found":true,"locations":[],"written":[]}""")]
    public async Task FallsBackToTheReferencesRequestWhenTheExtensionAnswerIsInvalidAsync(
        string answer
    )
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        var found = await WriteFileAsync(Root, FOUND_FILE, "class B {}\n");
        await using var session = CreateSession(
            Root,
            Answering(
                new(StringComparer.Ordinal)
                {
                    [LspMethods.SERVER_ACTIVATE_EXTENSION] = ActivatedHandlers(),
                    [LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE] = new JsonObject
                    {
                        ["response"] = answer,
                    },
                    [LspMethods.TEXT_DOCUMENT_REFERENCES] = new JsonArray(Location(found)),
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(Request(LspAction.References), TestCt);

        ResponseText(response).Should().Be($"{FOUND_FILE} 1:1");
    }

    [Theory]
    [InlineData(RoslynConnection.REQUEST_CANCELLED_CODE, 2)]
    [InlineData(RoslynConnection.SERVER_CANCELLED_CODE, 2)]
    [InlineData(HANDLER_FAILURE_CODE, 1)]
    public async Task DispatchFailureDisablesTheExtensionUnlessRoslynCancelledAsync(
        int errorCode,
        int expectedDispatches
    )
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        var found = await WriteFileAsync(Root, FOUND_FILE, "class B {}\n");
        var answers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [LspMethods.SERVER_ACTIVATE_EXTENSION] = ActivatedHandlers(),
            [LspMethods.TEXT_DOCUMENT_REFERENCES] = new JsonArray(Location(found)),
        };
        var dispatches = 0;
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                {
                    if (method is not LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE)
                        return Task.FromResult(answers.GetValueOrDefault(method));

                    Interlocked.Increment(ref dispatches);
                    throw new LocalRpcException("The extension failed.") { ErrorCode = errorCode };
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var first = await tool.ExecuteAsync(Request(LspAction.References), TestCt);
        var second = await tool.ExecuteAsync(Request(LspAction.References), TestCt);

        ResponseText(first).Should().Be($"{FOUND_FILE} 1:1");
        ResponseText(second).Should().Be($"{FOUND_FILE} 1:1");
        dispatches.Should().Be(expectedDispatches);
    }

    [Fact]
    public async Task ExpressionFailureReturnsAnErrorAndDisablesNoHandlerAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        var found = await WriteFileAsync(Root, FOUND_FILE, "class B {}\n");
        var expressionDispatches = 0;
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, parameters) =>
                {
                    if (
                        parameters?["messageName"]?.GetValue<string>()
                        is RoslynConnection.EXPRESSION_MESSAGE
                    )
                    {
                        Interlocked.Increment(ref expressionDispatches);
                    }

                    return method switch
                    {
                        LspMethods.SERVER_ACTIVATE_EXTENSION => Task.FromResult<JsonNode?>(
                            ActivatedHandlers()
                        ),
                        LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE => DispatchAnswer(
                            parameters,
                            found
                        ),
                        _ => Task.FromResult<JsonNode?>(null),
                    };
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var first = await tool.ExecuteAsync(Request(LspAction.Expression), TestCt);
        var second = await tool.ExecuteAsync(Request(LspAction.Expression), TestCt);
        var references = await tool.ExecuteAsync(Request(LspAction.References), TestCt);

        first.Should().BeError(ErrorCodes.LSP);
        second.Should().BeError(ErrorCodes.LSP);
        expressionDispatches.Should().Be(2);
        ResponseText(references).Should().Be($"{FOUND_FILE} 1:1");
    }

    [Theory]
    [InlineData(true, EXTENSION_CALLER_FILE)]
    [InlineData(false, HIERARCHY_CALLER_FILE)]
    public async Task CallersComeFromTheExtensionOnlyWhileItIsActiveAsync(
        bool activated,
        string expectedFile
    )
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        var extensionCaller = await WriteFileAsync(Root, EXTENSION_CALLER_FILE, "class B {}\n");
        var hierarchyCaller = await WriteFileAsync(Root, HIERARCHY_CALLER_FILE, "class C {}\n");
        var answers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE] = new JsonObject
            {
                ["response"] = new JsonObject
                {
                    ["found"] = true,
                    ["calls"] = new JsonArray(IncomingCall(extensionCaller)),
                }.ToJsonString(),
            },
            [LspMethods.TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY] = new JsonArray(
                new JsonObject { ["name"] = CALLER_NAME, ["kind"] = (int)LspSymbolKind.Method }
            ),
            [LspMethods.CALL_HIERARCHY_INCOMING_CALLS] = new JsonArray(
                IncomingCall(hierarchyCaller)
            ),
        };
        if (activated)
            answers[LspMethods.SERVER_ACTIVATE_EXTENSION] = ActivatedHandlers();

        await using var session = CreateSession(Root, Answering(answers));
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(Request(LspAction.Callers), TestCt);

        var item = $"{expectedFile}:1:7 method {CALLER_NAME}";
        string[] expected = activated
            ? [item]
            :
            [
                item,
                "calls through base or interface members and compiler-inserted calls not included",
            ];
        ResultLines.Lines(ResponseText(response)).Should().Equal(expected);
    }

    [Fact]
    public async Task CalleesFallBackToTheOutgoingCallsRequestWhenTheExtensionFailsAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        var callee = await WriteFileAsync(Root, HIERARCHY_CALLER_FILE, "class C {}\n");
        var answers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [LspMethods.SERVER_ACTIVATE_EXTENSION] = ActivatedHandlers(),
            [LspMethods.TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY] = new JsonArray(
                new JsonObject { ["name"] = CALLER_NAME, ["kind"] = (int)LspSymbolKind.Method }
            ),
            [LspMethods.CALL_HIERARCHY_OUTGOING_CALLS] = new JsonArray(OutgoingCall(callee)),
        };
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    method is LspMethods.TEXT_DOCUMENT_DISPATCH_EXTENSION_MESSAGE
                        ? throw new LocalRpcException("The extension failed.")
                        {
                            ErrorCode = HANDLER_FAILURE_CODE,
                        }
                        : Task.FromResult(answers.GetValueOrDefault(method))
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(Request(LspAction.Callees), TestCt);

        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .Equal(
                $"{HIERARCHY_CALLER_FILE}:1:7 method {CALLER_NAME}",
                "compiler-inserted calls not included"
            );
    }

    [Fact]
    public async Task ImpactKeepsTheCompilerCallsMarkerOfAnEmptyCallersSectionAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        await using var session = CreateSession(
            Root,
            Answering(
                new(StringComparer.Ordinal)
                {
                    [LspMethods.TEXT_DOCUMENT_PREPARE_CALL_HIERARCHY] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = CALLER_NAME,
                            ["kind"] = (int)LspSymbolKind.Method,
                        }
                    ),
                    [LspMethods.CALL_HIERARCHY_INCOMING_CALLS] = new JsonArray(),
                    [LspMethods.TEXT_DOCUMENT_DEFINITION] = new JsonArray(),
                    [LspMethods.TEXT_DOCUMENT_REFERENCES] = new JsonArray(),
                    [LspMethods.TEXT_DOCUMENT_IMPLEMENTATION] = new JsonArray(),
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(Request(LspAction.Impact), TestCt);

        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .Equal(
                "## references: none",
                "## callers",
                "no callers",
                "reflection or code outside the workspace may still call the member",
                "calls through base or interface members and compiler-inserted calls not included",
                "## implementation: none"
            );
    }

    [Fact]
    public async Task UnusedSymbolsFailWithoutTheWorkspaceHandlerAsync()
    {
        await WriteFileAsync(Root, SOURCE_FILE, "class A {}\n");
        await using var session = CreateSession(
            Root,
            Answering(
                new(StringComparer.Ordinal)
                {
                    [LspMethods.SERVER_ACTIVATE_EXTENSION] = ActivatedHandlers(),
                }
            )
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Symbols,
                File = SOURCE_FILE,
                Unused = true,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.LSP);
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
            File = SOURCE_FILE,
            Line = 1,
            Character = 1,
        };

    private static JsonObject IncomingCall(string path) =>
        new()
        {
            ["from"] = new JsonObject
            {
                ["name"] = CALLER_NAME,
                ["kind"] = (int)LspSymbolKind.Method,
                ["uri"] = new Uri(path).AbsoluteUri,
                ["selectionRange"] = Range(0, 6),
            },
            ["fromRanges"] = new JsonArray(),
        };

    private static JsonObject OutgoingCall(string path) =>
        new()
        {
            ["to"] = new JsonObject
            {
                ["name"] = CALLER_NAME,
                ["kind"] = (int)LspSymbolKind.Method,
                ["uri"] = new Uri(path).AbsoluteUri,
                ["selectionRange"] = Range(0, 6),
            },
            ["fromRanges"] = new JsonArray(),
        };

    // The expression handler throws, and the references handler finds one location
    private static Task<JsonNode?> DispatchAnswer(JsonNode? parameters, string foundPath) =>
        parameters?["messageName"]?.GetValue<string>() is RoslynConnection.EXPRESSION_MESSAGE
            ? throw new LocalRpcException("The extension failed.")
            {
                ErrorCode = HANDLER_FAILURE_CODE,
            }
            : Task.FromResult<JsonNode?>(
                new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["found"] = true,
                        ["locations"] = new JsonArray(Location(foundPath)),
                        ["written"] = new JsonArray(),
                        ["generated"] = 0,
                    }.ToJsonString(),
                }
            );

    private static JsonObject ActivatedHandlers() =>
        new()
        {
            ["documentMessageHandlers"] = new JsonArray(
                JsonValue.Create(RoslynConnection.REFERENCES_MESSAGE),
                JsonValue.Create(RoslynConnection.CALLERS_MESSAGE),
                JsonValue.Create(RoslynConnection.CALLEES_MESSAGE),
                JsonValue.Create(RoslynConnection.EXPRESSION_MESSAGE)
            ),
        };
}
