// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using ModelContextProtocol.Protocol;

namespace RoslynBridge.Results;

internal static class ToolResponse
{
    extension(ToolError error)
    {
        public CallToolResult ToResponse() => Text(ResultText.Error(error), isError: true);
    }

    extension(ToolResult result)
    {
        public CallToolResult ToResponse(IReadOnlyList<string>? resolvedFiles = null)
        {
            var heading = ResultText.ResolvedFiles(resolvedFiles ?? []);
            var text =
                heading + result.Render(ResultText.MAXIMUM_RESPONSE_CHARACTERS - heading.Length);
            return text.Length <= ResultText.MAXIMUM_RESPONSE_CHARACTERS || result.MustDeliver
                ? Text(text, isError: false)
                : new ToolError(
                    ErrorCodes.RESULT_TOO_LARGE,
                    $"The result exceeds {ResultText.MAXIMUM_RESPONSE_CHARACTERS} characters. Narrow the query or lower limit."
                ).ToResponse();
        }
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };
}
