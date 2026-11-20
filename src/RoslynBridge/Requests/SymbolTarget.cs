// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Requests;

/// <summary>
/// A symbol name with an optional file, a one-based position in a file, or a line inside a declaration.
/// </summary>
internal closed record SymbolTarget
{
    internal const string SymbolDescription =
        "C# symbol name, such as Namespace.Type.Member, Type.Member or Type.Method(int). Replaces line and character. Add file to pick between same-named declarations. A constructor needs its parameter list.";
    internal const string FileDescription = "Workspace-relative file path.";
    internal const string LineDescription = "One-based line number.";
    internal const string CharacterDescription = "One-based UTF-16 character in the line.";

    /// <summary>
    /// A <see cref="NamedSymbol"/> or a <see cref="SymbolAt"/>.
    /// </summary>
    public static Result<SymbolTarget> From(
        string? symbol,
        string? file,
        int? line,
        int? character
    ) =>
        (symbol, file, line, character) switch
        {
            ({ } name, _, null, null) => new NamedSymbol(name, file),
            (null, { } path, int number and > 0, int column and > 0) => new SymbolAt(
                path,
                new SourcePosition(number, column)
            ),
            (not null, _, _, _) => ToolError.InvalidRequest(
                "Pass symbol with an optional file, or a file with a line and character."
            ),
            _ => ToolError.InvalidRequest(
                "Provide symbol, or a file with a one-based line and character."
            ),
        };

    /// <summary>
    /// A <see cref="NamedSymbol"/> or a <see cref="DeclarationLine"/>, which ignores the character.
    /// </summary>
    public static Result<SymbolTarget> Declaration(
        string? symbol,
        string? file,
        int? line,
        int? character
    ) =>
        (symbol, file, line, character) switch
        {
            ({ } name, _, null, null) => new NamedSymbol(name, file),
            (null, { } path, int number and > 0, _) => new DeclarationLine(path, number),
            (not null, _, _, _) => ToolError.InvalidRequest(
                "Pass symbol with an optional file, or a file with a line."
            ),
            _ => ToolError.InvalidRequest("Provide symbol, or a file with a one-based line."),
        };

    public SymbolTarget WithFile(FileResolver resolve) =>
        this switch
        {
            NamedSymbol named => named with { File = resolve(named.File) },
            SymbolAt at => at with { File = resolve(at.File) },
            DeclarationLine line => line with { File = resolve(line.File) },
            ResolvedAt => this,
        };
}

/// <param name="File">Picks between declarations of the same name.</param>
internal sealed record NamedSymbol(string Symbol, string? File) : SymbolTarget;

internal sealed record SymbolAt(string File, SourcePosition Position) : SymbolTarget;

/// <summary>
/// A location that the bridge already found, so it needs no lookup of its file.
/// </summary>
internal sealed record ResolvedAt(SourceLocation Location) : SymbolTarget;

/// <summary>
/// The declaration that contains <paramref name="Line"/>.
/// </summary>
internal sealed record DeclarationLine(string File, int Line) : SymbolTarget;
