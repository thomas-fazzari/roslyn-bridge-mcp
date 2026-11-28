// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Encodings.Web;

namespace RoslynBridge.Results;

/// <summary>
/// Positions are one-based <c>line:character</c> pairs. A line that names a symbol ends with the name, which can be sent back unchanged.
/// </summary>
internal static class ResultText
{
    internal const int MAXIMUM_RESPONSE_CHARACTERS = 32_000;
    internal const string RESOLVED_FILE_PREFIX = "resolved file: ";
    internal const string ERROR_PREFIX = "error ";
    internal const string INDENT = "  ";

    private const int MAXIMUM_FAILURE_MESSAGE_CHARACTERS = 2_000;

    // Room for the lines that close a preview or receipt after its files
    private const int CLOSING_RESERVE = 200;

    private static readonly JsonWriterOptions _jsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static string ResolvedFiles(IEnumerable<string> files) =>
        string.Concat(files.Select(file => $"{RESOLVED_FILE_PREFIX}{file}\n"));

    internal static string Error(ToolError error)
    {
        var text = new StringBuilder(ERROR_PREFIX).Append(error.Code);
        if (error.RpcCode is { } code)
            text.Append(CultureInfo.InvariantCulture, $" (LSP {code})");

        // A failed edit caps its message to leave room for the receipt
        var message =
            error is EditFailure
                ? error.Message[
                    ..Math.Min(error.Message.Length, MAXIMUM_FAILURE_MESSAGE_CHARACTERS)
                ]
                : error.Message;
        text.Append(": ").Append(OneLine(message)).Append('\n');
        foreach (var candidate in error.Candidates ?? [])
        {
            AppendSymbol(text, candidate, indent: string.Empty);
        }

        if (error is EditFailure failure)
            AppendFailure(text, failure);

        return Finish(text);
    }

    internal static string? ErrorCode(string text)
    {
        if (!text.StartsWith(ERROR_PREFIX, StringComparison.Ordinal))
            return null;

        var code = text.AsSpan(ERROR_PREFIX.Length);
        var end = code.IndexOfAny(' ', ':');
        return (end < 0 ? code : code[..end]).ToString();
    }

    /// <summary>
    /// Compact JSON, with non-ASCII characters left unescaped.
    /// </summary>
    internal static string Json(JsonNode? node)
    {
        if (node is null)
            return "null";

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, _jsonOptions))
        {
            node.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    internal static void AppendSymbolTree(StringBuilder text, SymbolItem symbol, string indent)
    {
        AppendSymbol(text, symbol, indent);
        if (symbol.Through is { } through)
            text.Append(INDENT).Append("through ").Append(through).Append('\n');

        if (symbol.Lines is { } lines)
        {
            AppendSourceLines(text, LinePositions(symbol), lines);
        }
        else if (symbol.Calls.Count > 0)
        {
            text.Append(INDENT)
                .Append("calls ")
                .AppendJoin(' ', symbol.Calls.Select(Position))
                .Append('\n');
        }

        foreach (var child in symbol.Children)
        {
            AppendSymbolTree(text, child, indent + INDENT);
        }
    }

    internal static void AppendSourceLines(
        StringBuilder text,
        IEnumerable<SourcePosition> positions,
        IReadOnlyDictionary<int, string> lines
    )
    {
        foreach (var position in positions)
        {
            text.Append(INDENT).Append(Position(position));
            if (lines.TryGetValue(position.Line, out var source) && source.Length > 0)
                text.Append(' ').Append(source);

            text.Append('\n');
        }
    }

    internal static void AppendSymbol(StringBuilder text, SymbolItem symbol, string indent)
    {
        text.Append(indent);
        if (Location(symbol) is { } location)
            text.Append(location).Append(' ');

        if (symbol.Kind is { } kind)
            text.Append(kind).Append(' ');

        text.Append(symbol.Name);
        if (symbol.Detail is { } detail)
            text.Append(" : ").Append(detail);

        if (symbol.Container is { } container)
            text.Append(" in ").Append(container);

        text.Append('\n');
    }

    // An offset past the last result leaves the page empty, which must not read as no results
    internal static void AppendPageEnd(
        StringBuilder text,
        int total,
        bool pageEmpty,
        int? nextOffset,
        string noun
    )
    {
        if (total > 0 && pageEmpty)
        {
            text.Append(
                CultureInfo.InvariantCulture,
                $"offset past the end: {total} {noun} in total\n"
            );
            return;
        }

        AppendEmptyOrTruncation(text, total, noun, nextOffset);
    }

    internal static void AppendEmptyOrTruncation(
        StringBuilder text,
        int total,
        string noun,
        int? nextOffset,
        bool limited = false
    )
    {
        if (total == 0)
        {
            text.Append("no ").Append(noun).Append('\n');
            return;
        }

        AppendTruncation(text, total, noun, nextOffset, limited);
    }

    /// <param name="limited">Whether a result without pages left items out.</param>
    internal static void AppendTruncation(
        StringBuilder text,
        int total,
        string noun,
        int? nextOffset,
        bool limited = false
    )
    {
        if (nextOffset is null && !limited)
            return;

        text.Append(CultureInfo.InvariantCulture, $"truncated: {total} {noun} in total");
        if (nextOffset is { } next)
            text.Append(CultureInfo.InvariantCulture, $", next offset: {next}");

        text.Append(". Raise limit or narrow the query.\n");
    }

    internal static string? Location(SymbolItem symbol)
    {
        var position = symbol.Position is { } first ? Position(first) : null;
        if (position is not null && symbol.End is { } end)
            position += "-" + Position(end);

        return (symbol.File?.Text, position) switch
        {
            ({ } file, { } start) => $"{file}:{start}",
            ({ } file, null) => file,
            (null, var start) => start,
        };
    }

    internal static string Position(SourcePosition position) =>
        string.Create(CultureInfo.InvariantCulture, $"{position.Line}:{position.Character}");

    internal static string Count(int count, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? singular : plural)}");

    internal static string OneLine(string text) => text.ReplaceLineEndings(" ").Trim();

    internal static string Finish(StringBuilder text) => text.ToString().TrimEnd('\n');

    /// <summary>
    /// Appends the blocks that fit <paramref name="budget"/> and returns how many it appended.
    /// </summary>
    internal static int AppendWithin(StringBuilder text, IEnumerable<string> blocks, int budget)
    {
        var shown = 0;
        foreach (var block in blocks)
        {
            if (text.Length + block.Length > budget - CLOSING_RESERVE)
                break;

            text.Append(block);
            shown++;
        }

        return shown;
    }

    internal static string OperationLine(FileOperation operation) =>
        operation.OldPath is { } oldPath
            ? $"{operation.Kind.Name} {oldPath} -> {operation.Path}\n"
            : $"{operation.Kind.Name} {operation.Path}\n";

    private static void AppendFailure(StringBuilder text, EditFailure failure)
    {
        text.Append("failed step: ").Append(failure.Phase.ToString().ToLowerInvariant());
        if (failure.FailedPath is { } path)
            text.Append(", path ").Append(path);

        if (failure.FailedOperation is { } operation)
            text.Append(", operation ").Append(operation.Name);

        text.Append('\n').Append(failure.Receipt.Render(MAXIMUM_RESPONSE_CHARACTERS - text.Length));
    }

    private static IEnumerable<SourcePosition> LinePositions(SymbolItem symbol)
    {
        if (symbol.Calls.Count > 0)
            return symbol.Calls;

        return symbol.Position is { } position ? [position] : [];
    }
}
