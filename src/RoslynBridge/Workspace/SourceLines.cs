// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Editing;

namespace RoslynBridge.Workspace;

/// <summary>
/// Reads the trimmed source lines of positions, and each file once.
/// </summary>
internal sealed class SourceLines
{
    internal const int MAXIMUM_LINE_CHARACTERS = 200;

    private readonly Dictionary<WorkspaceFilePath, FileLines?> _files = [];

    /// <summary>
    /// Returns null when the file is null or cannot be read.
    /// </summary>
    public Dictionary<int, string>? At(
        WorkspaceFilePath? file,
        IEnumerable<SourcePosition> positions
    ) =>
        Of(file) is not { } text
            ? null
            : positions
                .Select(position => position.Line)
                .Where(text.Contains)
                .Distinct()
                .ToDictionary(line => line, line => Trim(text[line]));

    /// <summary>
    /// Returns null when the file cannot be read.
    /// </summary>
    public static FileLines? TryReadLines(WorkspaceFilePath? file) =>
        file is not null && ReadLines(file) is FileLines lines ? lines : null;

    public static Result<FileLines> ReadLines(WorkspaceFilePath file)
    {
        string source;
        try
        {
            source = File.ReadAllText(file.FullPath);
        }
        catch (Exception exception) when (ToolError.IsIo(exception))
        {
            return ToolError.Io(exception.Message);
        }

        return new FileLines([
            .. TextEdits.GetLines(source).Select(line => source.Substring(line.Start, line.Length)),
        ]);
    }

    /// <summary>
    /// The untrimmed lines of the file, or null when the file is null or cannot be read.
    /// </summary>
    public FileLines? Of(WorkspaceFilePath? file)
    {
        if (file is null)
            return null;

        if (!_files.TryGetValue(file, out var text))
        {
            text = TryReadLines(file);
            _files[file] = text;
        }

        return text;
    }

    private static string Trim(string line)
    {
        var text = line.Trim();
        return text.Length <= MAXIMUM_LINE_CHARACTERS
            ? text
            : $"{text.AsSpan(0, MAXIMUM_LINE_CHARACTERS)}…";
    }
}
