// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class SessionDocumentTests : IDisposable
{
    private const string ChangedText = "class B {}";

    private readonly string _root = Directory
        .CreateTempSubdirectory("roslyn-bridge-mcp-documents-")
        .FullName;

    [Fact]
    public async Task ConcurrentOpensOfOneFileSendOneDidOpenAsync()
    {
        var path = await WriteFileAsync(_root, "Greeter.cs", "class A {}");
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(_root, FakeServer(servers.Add));
        await session.EnsureStartedAsync(TestCt);

        await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => session.OpenDocumentAsync(path, TestCt))
        );

        (await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidOpen))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task SendsSameLengthEditsButNotTouchedFilesAsync()
    {
        var path = await WriteFileAsync(_root, "Greeter.cs", "class A {}");
        var written = File.GetLastWriteTimeUtc(path);
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(_root, FakeServer(servers.Add));
        await session.EnsureStartedAsync(TestCt);
        await session.OpenDocumentAsync(path, TestCt);

        File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
        await session.SynchronizeAsync(TestCt);
        (await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidChange))
            .Should()
            .BeEmpty();

        await File.WriteAllTextAsync(path, ChangedText, TestCt);
        File.SetLastWriteTimeUtc(path, written.AddSeconds(2));
        await session.SynchronizeAsync(TestCt);

        var changes = await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidChange);
        var change = changes.Should().ContainSingle().Subject;
        change?["contentChanges"]?[0]?["text"]?.GetValue<string>().Should().Be(ChangedText);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static async Task<List<JsonNode?>> ReceivedAsync(
        RoslynSession session,
        FakeRoslynServer server,
        string method
    )
    {
        // Roslyn answers a request after the notifications that precede it
        await session.RequestAsync(
            LspMethods.WorkspaceSymbol,
            new JsonObject { ["query"] = string.Empty },
            TestCt
        );
        return
        [
            .. server
                .Notifications.Where(notification =>
                    string.Equals(notification.Method, method, StringComparison.Ordinal)
                )
                .Select(notification => notification.Parameters),
        ];
    }
}
