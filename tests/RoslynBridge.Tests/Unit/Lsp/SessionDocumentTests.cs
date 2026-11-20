// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Tests.Fakes;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class SessionDocumentTests : TempWorkspaceTests
{
    private const string ChangedText = "class B {}";
    private const int FileDeleted = 3;

    [Fact]
    public async Task ConcurrentOpensOfOneFileSendOneDidOpenAsync()
    {
        var path = await WriteFileAsync(Root, "Greeter.cs", "class A {}");
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();

        var opened = await Task.WhenAll(
            Enumerable
                .Range(0, 8)
                .Select(_ => session.OpenDocumentAsync(Resolved(Root, path), TestCt))
        );

        opened.Should().AllSatisfy(result => result.ErrorCode().Should().BeNull());

        (await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidOpen))
            .Should()
            .ContainSingle();
    }

    [Fact]
    public async Task FileGivenInAnotherCasingIsNeitherAnnouncedNorOpenedTwiceAsync()
    {
        SkipOnCaseSensitiveFileSystem();
        var path = await WriteFileAsync(Root, "Greeter.cs", "class A {}");
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();
        await RegisterWatchedFilesAsync(servers[0]);
        var other = Resolved(Root, "greeter.cs");

        (await session.SynchronizeNewFilesAsync([other], TestCt)).ErrorCode().Should().BeNull();
        (await session.OpenDocumentAsync(other, TestCt)).Succeeded();
        (await session.OpenDocumentAsync(Resolved(Root, path), TestCt)).Succeeded();
        (await session.SynchronizeWorkspaceAsync(TestCt)).Succeeded();

        (await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidOpen))
            .Should()
            .ContainSingle();
        (await ReceivedAsync(session, servers[0], LspMethods.WorkspaceDidChangeWatchedFiles))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task SendsSameLengthEditsButNotTouchedFilesAsync()
    {
        var path = await WriteFileAsync(Root, "Greeter.cs", "class A {}");
        var written = File.GetLastWriteTimeUtc(path);
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();
        (await session.OpenDocumentAsync(Resolved(Root, path), TestCt)).Succeeded();

        File.SetLastWriteTimeUtc(path, written.AddSeconds(1));
        (await session.SynchronizeAsync(TestCt)).ErrorCode().Should().BeNull();
        (await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidChange))
            .Should()
            .BeEmpty();

        await File.WriteAllTextAsync(path, ChangedText, TestCt);
        File.SetLastWriteTimeUtc(path, written.AddSeconds(2));
        (await session.SynchronizeAsync(TestCt)).ErrorCode().Should().BeNull();

        var changes = await ReceivedAsync(session, servers[0], LspMethods.TextDocumentDidChange);
        var change = changes.Should().ContainSingle().Subject;
        change!["contentChanges"]![0]!["text"]!.GetValue<string>().Should().Be(ChangedText);
    }

    [Fact]
    public async Task DeletedFileClosesItsDocumentAndIsReportedAsDeletedAsync()
    {
        var path = await WriteFileAsync(Root, "Greeter.cs", "class A {}");
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();
        (await session.OpenDocumentAsync(Resolved(Root, path), TestCt)).Succeeded();
        await RegisterWatchedFilesAsync(servers[0]);

        File.Delete(path);
        (await session.SynchronizeWorkspaceAsync(TestCt)).Succeeded();

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

    [Fact]
    public async Task FileWatchRegisteredDuringShutdownStaysWithItsProcessAsync()
    {
        var servers = new List<FakeRoslynServer>();
        await using var session = CreateSession(Root, FakeServer(servers.Add));
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();
        servers[0].OnShutdown = () => RegisterWatchedFilesAsync(servers[0]);
        session.RequireReload(session.Generation);
        (await session.EnsureStartedAsync(TestCt)).ErrorCode().Should().BeNull();

        var path = await WriteFileAsync(Root, "Added.cs", "class Added {}");
        (await session.SynchronizeFilesAsync([Resolved(Root, path)], TestCt))
            .ErrorCode()
            .Should()
            .BeNull();

        (await ReceivedAsync(session, servers[1], LspMethods.WorkspaceDidChangeWatchedFiles))
            .Should()
            .BeEmpty();
    }

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
