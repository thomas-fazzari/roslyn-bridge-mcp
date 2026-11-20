// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Results;

internal closed record ToolResult
{
    /// <summary>
    /// Whether the result reaches the client above the response limit. It must then fit its rendering to the budget.
    /// </summary>
    public virtual bool MustDeliver => false;

    /// <summary>
    /// Whether the result found nothing, which a report section shows as none.
    /// </summary>
    public virtual bool IsEmpty => false;

    /// <param name="budget">Characters a preview or receipt may use. They drop trailing files to fit.</param>
    public string Render(int budget)
    {
        var text = new StringBuilder();
        Write(text, budget);
        return ResultText.Finish(text);
    }

    internal abstract void Write(StringBuilder text, int budget);
}

internal static class ToolResults
{
    extension<T>(Result<T> result)
        where T : ToolResult
    {
        public Result<ToolResult> AsToolResult() => result.Map<ToolResult>(value => value);
    }
}
