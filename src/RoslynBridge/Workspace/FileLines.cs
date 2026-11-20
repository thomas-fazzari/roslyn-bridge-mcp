// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Workspace;

/// <summary>
/// The untrimmed lines of a source file, addressed by one-based line number.
/// </summary>
internal sealed class FileLines(string[] lines)
{
    public int Count => lines.Length;

    public string this[int line] => lines[line - 1];

    public bool Contains(int line) => line >= 1 && line <= lines.Length;
}
