// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class ProjectIssueTests : TempWorkspaceTests
{
    [Fact(Timeout = 30_000)]
    public async Task StatusListsLoadIssuesUntilTheNextReloadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        using var tool = CreateTool(Root, session);
        (await session.EnsureStartedAsync(cancellationToken)).ErrorCode().Should().BeNull();
        var broken = Path.Combine(Root, "Broken", "Broken.csproj");
        var outside = Path.Combine(Path.GetTempPath(), "Outside", "My App.csproj");

        var logs = new[]
        {
            $"[LanguageServerProjectSystem] Error while loading {broken}: The import of {broken} was not found.\nMore.",
            $"[Scope] [Category] Warning while loading {broken}: A later warning",
            $"Warning while loading {outside}: Package is deprecated",
            "Error while loading something that is no project",
        };
        foreach (var log in logs)
        {
            await servers[0].LogMessageAsync(log, cancellationToken);
        }

        (await StatusAsync(tool))
            .Should()
            .EndWith(
                $"project not loaded: Broken/Broken.csproj: The import of Broken/Broken.csproj was not found.\nproject warning: {WorkspacePaths.FileUri(outside)}: Package is deprecated"
            );

        await tool.ExecuteAsync(new LspRequest { Action = LspAction.Reload }, cancellationToken);

        (await StatusAsync(tool))
            .Should()
            .NotContain("project not loaded")
            .And.NotContain("project warning");
    }

    private static async Task<string> StatusAsync(ToolClient tool) =>
        ResponseText(await tool.ExecuteAsync(new LspRequest { Action = LspAction.Status }, TestCt));
}
