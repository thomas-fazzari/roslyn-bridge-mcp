// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using RoslynBridge.Changes;
using RoslynBridge.Editing;
using RoslynBridge.Lsp;
using RoslynBridge.Queries;
using RoslynBridge.Results;
using RoslynBridge.Settings;
using RoslynBridge.Symbols;
using RoslynBridge.Tests.Fakes;
using RoslynBridge.Tools;

namespace RoslynBridge.Tests.Unit;

/// <summary>
/// Provides the test cancellation token, creates sessions and tools, writes workspace files and builds LSP workspace edits.
/// </summary>
internal static class TestHelpers
{
    public static CancellationToken TestCt => TestContext.Current.CancellationToken;

    public static RoslynSession CreateSession(
        string root,
        RoslynLauncher launcher,
        BridgeOptions? options = null
    ) =>
        new(
            Monitor(options ?? new BridgeOptions { WorkspaceRoot = root }),
            new WorkspacePaths(root),
            launcher,
            NullLogger<RoslynSession>.Instance
        );

    public static SettingsMonitor Monitor(BridgeOptions options) => new(() => options);

    public static LspTool CreateTool(
        string root,
        RoslynSession session,
        BridgeOptions? options = null,
        SettingsMonitor? settings = null
    )
    {
        settings ??= Monitor(options ?? new BridgeOptions { WorkspaceRoot = root });
        var paths = new WorkspacePaths(root);
        var queries = new LspQueries(session, paths, new SymbolResolver(session, paths));
        return new LspTool(
            settings,
            paths,
            session,
            queries,
            new LspChanges(session, paths, new WorkspaceEditService(paths), queries),
            NullLogger<LspTool>.Instance
        );
    }

    public static WorkspaceSnapshot Snapshot(string root) =>
        new(
            new WorkspacePaths(root)
                .EnumerateWatchedFiles()
                .ToDictionary(file => file.Path, file => file.Stamp, StringComparer.Ordinal),
            new Dictionary<string, int>(StringComparer.Ordinal)
        );

    public static RoslynLauncher FakeServer(Action<FakeRoslynServer>? configure = null) =>
        (_, _) =>
        {
            var server = new FakeRoslynServer();
            configure?.Invoke(server);
            return server;
        };

    public static string ResponseText(CallToolResult response)
    {
        response.StructuredContent.Should().BeNull();
        return response.Content.OfType<TextContentBlock>().Single().Text;
    }

    public static string? ErrorCode(CallToolResult response)
    {
        var text = ResponseText(response);
        if (!text.StartsWith("error ", StringComparison.Ordinal))
        {
            return null;
        }

        var end = text.IndexOfAny([' ', ':'], "error ".Length);
        return text["error ".Length..end];
    }

    public static async Task<string> WriteFileAsync(string root, string relativePath, string text)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, TestCt);
        return path;
    }

    public static async Task AssertFileTextAsync(string path, string expected)
    {
        var actual = await File.ReadAllTextAsync(path, TestCt);

        actual.Should().Be(expected, "file {0} should contain the expected text", path);
    }

    public static JsonObject WorkspaceChanges(string path, params JsonObject[] edits) =>
        new()
        {
            ["changes"] = new JsonObject { [new Uri(path).AbsoluteUri] = new JsonArray(edits) },
        };

    public static JsonObject DocumentChanges(params JsonObject[] operations) =>
        new() { ["documentChanges"] = new JsonArray(operations) };

    public static JsonObject DocumentEdit(string path, int? version, params JsonObject[] edits) =>
        new()
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = new Uri(path).AbsoluteUri,
                ["version"] = version,
            },
            ["edits"] = new JsonArray(edits),
        };

    public static JsonObject CreateFile(string path) =>
        new() { ["kind"] = FileOperation.Create, ["uri"] = new Uri(path).AbsoluteUri };

    public static JsonObject RenameFile(string source, string destination) =>
        new()
        {
            ["kind"] = FileOperation.Rename,
            ["oldUri"] = new Uri(source).AbsoluteUri,
            ["newUri"] = new Uri(destination).AbsoluteUri,
        };

    public static JsonObject DeleteFile(string path) =>
        new() { ["kind"] = FileOperation.Delete, ["uri"] = new Uri(path).AbsoluteUri };

    public static JsonObject TextEdit(
        int startLine,
        int startCharacter,
        int endLine,
        int endCharacter,
        string text
    ) =>
        new()
        {
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = startLine, ["character"] = startCharacter },
                ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endCharacter },
            },
            ["newText"] = text,
        };
}
