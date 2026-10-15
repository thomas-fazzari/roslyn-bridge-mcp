// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class ClientCallbacksTests : IDisposable
{
    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-callback-tests-")
        .FullName;

    [Fact]
    public async Task ApplyEditForwardsTheWorkspaceEditToTheFileEditorAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class Before {}");
        var paths = new WorkspacePaths(_root);
        var editor = new WorkspaceEditService(paths);
        var snapshot = await editor.CaptureAsync(TestCt);
        await using var session = CreateSession(paths);
        session.ApplyWorkspaceEditAsync = async (edit, token) =>
        {
            await editor.ApplyAsync(edit, snapshot, token);
            return new JsonObject { ["applied"] = true };
        };
        var callbacks = CreateCallbacks(session, paths);

        var response = await callbacks.ApplyEditAsync(
            new JsonObject { ["label"] = "Rename class", ["edit"] = RenameClassEdit(source) },
            TestCt
        );

        response["applied"]!.GetValue<bool>().Should().BeTrue();
        await AssertFileTextAsync(source, "class After {}");
    }

    [Fact]
    public async Task ApplyEditOutsideAToolCallIsNotAppliedAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class Before {}");
        var paths = new WorkspacePaths(_root);
        await using var session = CreateSession(paths);
        var callbacks = CreateCallbacks(session, paths);

        var response = await callbacks.ApplyEditAsync(
            new JsonObject { ["edit"] = RenameClassEdit(source) },
            TestCt
        );

        response["applied"]!.GetValue<bool>().Should().BeFalse();
        await AssertFileTextAsync(source, "class Before {}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RoslynSession CreateSession(WorkspacePaths paths) =>
        new(
            new BridgeOptions { WorkspaceRoot = paths.Root },
            paths,
            RoslynProcess.Start,
            NullLogger<RoslynSession>.Instance
        );

    private static ClientCallbacks CreateCallbacks(RoslynSession session, WorkspacePaths paths) =>
        new(session, paths, NullLogger<RoslynSession>.Instance, new TaskCompletionSource());

    private static JsonObject RenameClassEdit(string source) =>
        WorkspaceChanges(source, TextEdit(0, 6, 0, 12, "After"));
}
