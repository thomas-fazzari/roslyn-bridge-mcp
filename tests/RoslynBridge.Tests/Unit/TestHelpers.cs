// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using RoslynBridge.Editing;

namespace RoslynBridge.Tests.Unit;

/// <summary>
/// Provides the test cancellation token, writes workspace files and builds LSP workspace edits.
/// </summary>
internal static class TestHelpers
{
    public static CancellationToken TestCt => TestContext.Current.CancellationToken;

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
        new()
        {
            ["kind"] = WorkspaceEditService.CreateFileOperation,
            ["uri"] = new Uri(path).AbsoluteUri,
        };

    public static JsonObject RenameFile(string source, string destination) =>
        new()
        {
            ["kind"] = WorkspaceEditService.RenameFileOperation,
            ["oldUri"] = new Uri(source).AbsoluteUri,
            ["newUri"] = new Uri(destination).AbsoluteUri,
        };

    public static JsonObject DeleteFile(string path) =>
        new()
        {
            ["kind"] = WorkspaceEditService.DeleteFileOperation,
            ["uri"] = new Uri(path).AbsoluteUri,
        };

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
