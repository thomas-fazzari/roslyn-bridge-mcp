// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using RoslynBridge.Lsp;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class ClientCallbacksTests : TempWorkspaceTests
{
    [Fact]
    public async Task ApplyEditOutsideAToolCallIsNotAppliedAsync()
    {
        var source = await WriteFileAsync(Root, SourceFile, "class Before {}");
        var callbacks = new ClientCallbacks(new WorkspacePaths(Root), NullLogger.Instance);

        var response = await callbacks.ApplyEditAsync(
            new ApplyWorkspaceEditParams(RenameClassEdit(source)),
            TestCt
        );

        response.Applied.Should().BeFalse();
        await AssertFileTextAsync(source, "class Before {}");
    }

    private static JsonObject RenameClassEdit(string source) =>
        WorkspaceChanges(source, TextEdit(0, 6, 0, 12, "After"));
}
