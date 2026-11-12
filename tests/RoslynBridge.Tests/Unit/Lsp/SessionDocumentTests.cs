// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class SessionDocumentTests : IDisposable
{
    private const string ChangedText = "class B {}";
    private const int FileDeleted = 3;

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
        change!["contentChanges"]![0]!["text"]!.GetValue<string>().Should().Be(ChangedText);
    }

    [Fact]
    public async Task DeletedFileClosesItsDocumentAndIsReportedAsDeletedAsync()
    {
        var path = await WriteFileAsync(_root, "Greeter.cs", "class A {}");
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(_root, FakeServer(servers.Add));
        await session.EnsureStartedAsync(TestCt);
        await session.OpenDocumentAsync(path, TestCt);
        await RegisterWatchedFilesAsync(servers[0]);

        File.Delete(path);
        await session.SynchronizeWorkspaceAsync(TestCt);

        var uri = new Uri(path).AbsoluteUri;
        var closed = await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidClose);
        closed
            .Select(parameters => parameters!["textDocument"]!["uri"]!.GetValue<string>())
            .Should()
            .Equal(uri);
        var watched = await ReceivedAsync(
            session,
            servers[0],
            LspMethods.WorkspaceDidChangeWatchedFiles
        );
        watched
            .SelectMany(parameters => parameters!["changes"]!.AsArray())
            .Select(change =>
                (Uri: change!["uri"]!.GetValue<string>(), Type: change["type"]!.GetValue<int>())
            )
            .Should()
            .Contain((uri, FileDeleted));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static Task<JsonNode?> RegisterWatchedFilesAsync(FakeRoslynServer server) =>
        server.RequestClientAsync(
            LspMethods.ClientRegisterCapability,
            new JsonObject
            {
                ["registrations"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "watched-files",
                        ["method"] = LspMethods.WorkspaceDidChangeWatchedFiles,
                    }
                ),
            },
            TestCt
        );

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
