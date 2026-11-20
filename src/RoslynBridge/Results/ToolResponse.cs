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
                heading + result.Render(ResultText.MaximumResponseCharacters - heading.Length);
            if (text.Length <= ResultText.MaximumResponseCharacters || result.MustDeliver)
                return Text(text, isError: false);

            return new ToolError(
                ErrorCodes.ResultTooLarge,
                $"The result exceeds {ResultText.MaximumResponseCharacters} characters. Narrow the query or lower limit."
            ).ToResponse();
        }
    }

    private static CallToolResult Text(string text, bool isError) =>
        new() { IsError = isError, Content = [new TextContentBlock { Text = text }] };
}
