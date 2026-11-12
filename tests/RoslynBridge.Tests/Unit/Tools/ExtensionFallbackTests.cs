// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using static RoslynBridge.Tests.Fakes.LspLocationJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class ExtensionFallbackTests : IDisposable
{
    private const string SourceFile = "Source.cs";
    private const string FoundFile = "Found.cs";

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-extension-fallback-")
        .FullName;

    [Fact]
    public async Task FallsBackToTheReferencesRequestWhenTheExtensionAnswersAnEmptyResponseAsync()
    {
        await WriteFileAsync(_root, SourceFile, "class A {}\n");
        var found = await WriteFileAsync(_root, FoundFile, "class B {}\n");
        var answers = new Dictionary<string, JsonNode>(StringComparer.Ordinal)
        {
            [LspMethods.ServerActivateExtension] = ActivatedHandlers(),
            [LspMethods.TextDocumentDispatchExtensionMessage] = new JsonObject
            {
                ["response"] = string.Empty,
            },
            [LspMethods.TextDocumentReferences] = new JsonArray(Location(found)),
        };
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) => Task.FromResult(answers.GetValueOrDefault(method))
            )
        );
        using var tool = CreateTool(_root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.References,
                File = SourceFile,
                Line = 1,
                Character = 1,
            },
            TestCt
        );

        ResponseText(response).Should().Be($"{FoundFile} 1:1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static JsonObject ActivatedHandlers() =>
        new()
        {
            ["documentMessageHandlers"] = new JsonArray(
                JsonValue.Create("RoslynBridge.Extension.ReferencesHandler"),
                JsonValue.Create("RoslynBridge.Extension.CallersHandler")
            ),
        };
}
