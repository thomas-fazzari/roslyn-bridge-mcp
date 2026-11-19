// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Results;

namespace RoslynBridge.Editing;

[DebuggerDisplay("{Path}, {Content?.Length ?? 0} bytes, original {Original?.Length ?? 0} bytes")]
internal sealed class EditedDocument(string path, byte[]? original)
{
    internal const int MaximumPreviewCharacters = 2_000;

    private static readonly Encoding _utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true
    );
    private static readonly Encoding _utf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidBytes: true
    );
    private static readonly Encoding _utf16BigEndian = new UnicodeEncoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidBytes: true
    );
    private static readonly Encoding _utf32LittleEndian = new UTF32Encoding(
        bigEndian: false,
        byteOrderMark: true,
        throwOnInvalidCharacters: true
    );
    private static readonly Encoding _utf32BigEndian = new UTF32Encoding(
        bigEndian: true,
        byteOrderMark: true,
        throwOnInvalidCharacters: true
    );

    public string Path { get; set; } = path;

    public string OriginalPath { get; } = path;

    public bool CaseOnlyRename => !StringComparer.Ordinal.Equals(Path, OriginalPath);

    public byte[]? Original { get; } = original;

    /// <summary>
    /// The file whose Unix mode the written content keeps, or null for the default mode.
    /// </summary>
    public string? ModeSource { get; set; } = original is null ? null : path;

    public byte[]? Content { get; set; } = original;

    public bool Changed => CaseOnlyRename || !SameBytes(Original, Content);

    public Result Apply(TextEdit?[] edits)
    {
        if (Content is not { } content)
            return ToolError.InvalidLspData($"The file does not exist: {Path}");

        var (encoding, preamble) = GetEncoding(content);
        if (Decode(content).IsError(out var decodeError, out var source))
            return decodeError;

        if (TextEdits.Apply(source, edits).IsError(out var editError, out var edited))
            return editError;

        var length = encoding.GetByteCount(edited);
        if (length + preamble > WorkspaceEditService.MaximumFileBytes)
            return ToolError.InvalidRequest($"The edited file exceeds the size limit: {Path}");

        var bytes = new byte[length + preamble];
        content.AsSpan(0, preamble).CopyTo(bytes);
        encoding.GetBytes(edited.AsSpan(), bytes.AsSpan(preamble));
        Content = bytes;
        return Result.Success;
    }

    public Result<FilePreview> Describe(WorkspacePaths paths)
    {
        if (Decode(Original).IsError(out var beforeError, out var before))
            return beforeError;

        if (Decode(Content).IsError(out var afterError, out var after))
            return afterError;

        var preview = TextPreview.Create(before, after);
        var kind = (Original, Content, CaseOnlyRename) switch
        {
            (null, _, _) => FileOperation.Create,
            (_, null, _) => FileOperation.Delete,
            (_, _, true) => FileOperation.Rename,
            _ => FileOperation.Change,
        };
        return new FilePreview(
            new FileOperation(
                paths.RelativePath(Path),
                kind,
                CaseOnlyRename ? paths.RelativePath(OriginalPath) : null
            ),
            preview.Changes,
            preview.Truncated
        );
    }

    private Result<string> Decode(byte[]? content)
    {
        if (content is null)
            return string.Empty;

        var (encoding, preamble) = GetEncoding(content);
        try
        {
            return encoding.GetString(content, preamble, content.Length - preamble);
        }
        catch (DecoderFallbackException)
        {
            return ToolError.InvalidRequest(
                $"The file is not valid {encoding.WebName} text: {Path}"
            );
        }
    }

    private static (Encoding Encoding, int Preamble) GetEncoding(byte[] content)
    {
        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith(_utf32BigEndian.Preamble))
            return (_utf32BigEndian, _utf32BigEndian.Preamble.Length);

        if (bytes.StartsWith(_utf32LittleEndian.Preamble))
            return (_utf32LittleEndian, _utf32LittleEndian.Preamble.Length);

        if (bytes.StartsWith(_utf8.Preamble))
            return (_utf8, _utf8.Preamble.Length);

        if (bytes.StartsWith(_utf16LittleEndian.Preamble))
            return (_utf16LittleEndian, _utf16LittleEndian.Preamble.Length);

        return bytes.StartsWith(_utf16BigEndian.Preamble)
            ? (_utf16BigEndian, _utf16BigEndian.Preamble.Length)
            : (_utf8, 0);
    }

    private static bool SameBytes(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}
