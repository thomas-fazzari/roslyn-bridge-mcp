// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;

namespace RoslynBridge.Tests.Fakes;

/// <summary>
/// Answers document diagnostic pulls with a report and its result id, and answers <c>unchanged</c>
/// to that result id, as Roslyn does while a document stays the same.
/// </summary>
internal static class FakeDiagnosticServer
{
    internal const string ResultId = "report-1";

    /// <param name="code">The code of the one warning that each report holds.</param>
    /// <param name="sent">Receives the previous result id of each pull, or null when a pull sends none.</param>
    /// <returns>A launcher that starts a fake server answering diagnostic pulls.</returns>
    public static RoslynLauncher WithResultIds(string code, ConcurrentQueue<string?> sent) =>
        (_, _) =>
            new FakeRoslynServer
            {
                Handle = (method, parameters) =>
                {
                    if (method is not LspMethods.TextDocumentDiagnostic)
                    {
                        return Task.FromResult<JsonNode?>(null);
                    }

                    var previous = parameters?["previousResultId"]?.GetValue<string>();
                    sent.Enqueue(previous);
                    return Task.FromResult<JsonNode?>(
                        previous is ResultId
                            ? new JsonObject { ["kind"] = "unchanged", ["resultId"] = ResultId }
                            : Report(code)
                    );
                },
            };

    public static JsonObject FullReport(JsonArray items) =>
        new() { ["kind"] = "full", ["items"] = items };

    private static JsonObject Report(string code) =>
        new()
        {
            ["kind"] = "full",
            ["resultId"] = ResultId,
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["range"] = LspLocationJson.Range(0, 0),
                    ["severity"] = (int)DiagnosticSeverity.Warning,
                    ["code"] = code,
                    ["message"] = $"Message {code}",
                }
            ),
        };
}
