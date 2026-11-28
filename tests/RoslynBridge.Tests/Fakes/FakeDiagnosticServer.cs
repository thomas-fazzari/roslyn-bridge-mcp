// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;

namespace RoslynBridge.Tests.Fakes;

/// <summary>
/// Answers <c>unchanged</c> to a known result id, as Roslyn does while a document stays the same.
/// </summary>
internal static class FakeDiagnosticServer
{
    internal const string RESULT_ID = "report-1";

    /// <param name="sent">Receives the previous result id of each pull, or null when a pull sends none.</param>
    public static RoslynLauncher WithResultIds(string code, ConcurrentQueue<string?> sent) =>
        (_, _) =>
            new FakeRoslynServer
            {
                Handle = (method, parameters) =>
                {
                    if (method is not LspMethods.TEXT_DOCUMENT_DIAGNOSTIC)
                        return Task.FromResult<JsonNode?>(null);

                    var previous = parameters?["previousResultId"]?.GetValue<string>();
                    sent.Enqueue(previous);
                    return Task.FromResult<JsonNode?>(
                        previous is RESULT_ID
                            ? new JsonObject { ["kind"] = "unchanged", ["resultId"] = RESULT_ID }
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
            ["resultId"] = RESULT_ID,
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
