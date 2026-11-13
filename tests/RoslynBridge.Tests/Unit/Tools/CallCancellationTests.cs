// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Settings;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class CallCancellationTests : TempWorkspaceTests
{
    [Fact(Timeout = 30_000)]
    public async Task CallWithoutAnAnswerEndsOnTheRequestTimeoutAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteFileAsync(Root, SourceFile, "class A {}\n");
        var options = new BridgeOptions
        {
            WorkspaceRoot = Root,
            RequestTimeout = TimeSpan.FromMilliseconds(200),
        };
        var unanswered = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    method is LspMethods.TextDocumentReferences
                        ? unanswered.Task
                        : Task.FromResult<JsonNode?>(null)
            ),
            options
        );
        using var tool = CreateTool(Root, session, options);
        await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Capabilities },
            cancellationToken
        );

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.References,
                File = SourceFile,
                Line = 1,
                Character = 1,
            },
            cancellationToken
        );

        response.Should().BeError(ErrorCodes.Timeout);
        unanswered.SetResult(null);
    }

    [Fact]
    public async Task RequestThatRoslynCancelsFailsWithAnLspErrorAsync()
    {
        await WriteFileAsync(Root, SourceFile, "class A {}\n");
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    method is LspMethods.TextDocumentReferences
                        ? throw new LocalRpcException("The request was cancelled.")
                        {
                            ErrorCode = RoslynSession.RequestCancelledCode,
                        }
                        : Task.FromResult<JsonNode?>(null)
            )
        );
        using var tool = CreateTool(Root, session);

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

        response.Should().BeError(ErrorCodes.Lsp);
    }

    [Fact(Timeout = 30_000)]
    public async Task CancellationDuringAnAppliedEditReportsTheWrittenFilesAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, "class A {}\n");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        var afterTheWrite = new TaskCompletionSource<JsonNode?>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        await using var session = CreateSession(
            Root,
            FakeServer(server =>
            {
                server.Capabilities = LspCapabilityJson.WillRenameFiles();
                // The bridge asks for the project contexts only after it wrote the files
                server.Handle = async (method, _) =>
                {
                    if (method is not LspMethods.TextDocumentGetProjectContexts)
                    {
                        return null;
                    }

                    await cancellation.CancelAsync();
                    return await afterTheWrite.Task;
                };
            })
        );
        using var tool = CreateTool(Root, session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.RenameFile,
                File = SourceFile,
                NewName = RenamedFile,
                Apply = true,
            },
            cancellation.Token
        );

        response.Should().BeError(ErrorCodes.Cancelled);
        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .Contain($"create {RenamedFile}")
            .And.Contain($"delete {SourceFile}");
        File.Exists(source).Should().BeFalse();
        File.Exists(Path.Combine(Root, RenamedFile)).Should().BeTrue();
        afterTheWrite.SetResult(null);
    }
}
