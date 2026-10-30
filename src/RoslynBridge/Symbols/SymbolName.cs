// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Buffers;
using RoslynBridge.Lsp;

namespace RoslynBridge.Symbols;

/// <summary>
/// A dotted C# symbol name with an optional parameter list, such as <c>Sample.Box&lt;T&gt;.Put(int)</c>.
/// Segments keep the generic arity, as in <c>Box`1</c>, and drop the argument names.
/// </summary>
internal sealed record SymbolName(ImmutableArray<string> Segments, string? Parameters)
{
    private const string OperatorKeyword = "operator";
    private static readonly SearchValues<char> _operatorCharacters = SearchValues.Create(
        "<>=!+-*/%&|^~"
    );
    private static readonly SearchValues<char> _brackets = SearchValues.Create("()[]");

    /// <summary>
    /// Parses a requested name or a Roslyn document symbol name, or returns null for an invalid name.
    /// A trailing type, as in <c>Value : int</c> or <c>Put(int) : void</c>, is removed.
    /// </summary>
    public static SymbolName? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var name = WithoutType(text);

        string? parameters = null;
        if (name[^1] is ')' or ']')
        {
            var open = MatchingOpen(name);
            if (open < 0)
            {
                return null;
            }

            parameters = Normalize(name[open..]);
            name = name[..open];
        }

        var segments = SplitAtTopLevel(name, '.')
            .Select(segment => WithArity(segment.Trim()))
            .ToImmutableArray();
        return segments.Any(segment =>
            segment.Length == 0 || segment.AsSpan().ContainsAny(_brackets)
        )
            ? null
            : new SymbolName(segments, parameters);
    }

    /// <summary>
    /// Removes the trailing type that Roslyn appends to member names, as in <c>Put(int) : void</c>.
    /// </summary>
    public static string WithoutType(string text)
    {
        var name = text.Trim();
        var typeSeparator = IndexAtTopLevel(name, " : ");
        return typeSeparator >= 0 ? name[..typeSeparator] : name;
    }

    public bool IsCtor => Parameters is not null && NamesItsType;

    /// <returns>The constructor kind when Roslyn reports this constructor as a method, otherwise the reported kind.</returns>
    public int? CorrectedKind(int? reported) =>
        reported is LspSymbolKinds.Method && NamesItsType ? LspSymbolKinds.Constructor : reported;

    // A member cannot have the name of its type, except a constructor
    private bool NamesItsType =>
        Segments is [.., var type, var member]
        && string.Equals(BaseName(type), member, StringComparison.Ordinal);

    /// <summary>
    /// Returns whether this requested name selects a declared symbol.
    /// The requested segments must end the declared segments, and a requested parameter list must match.
    /// A loose match also accepts a segment without its generic arity and a parameter type without its trailing <c>?</c>.
    /// </summary>
    public bool Selects(SymbolName declared, bool exact) =>
        Segments.Length <= declared.Segments.Length
        && Segments
            .Zip(declared.Segments[^Segments.Length..])
            .All(pair => SegmentSelects(pair.First, pair.Second, exact))
        && (Parameters is null || ParametersSelect(Parameters, declared.Parameters, exact));

    /// <summary>
    /// Returns a segment without its generic arity, as written in source.
    /// </summary>
    public static string BaseName(string segment)
    {
        var arity = segment.IndexOf('`', StringComparison.Ordinal);
        return arity < 0 ? segment : segment[..arity];
    }

    /// <summary>
    /// Removes a trailing parameter list, as in <c>Run(int)</c>.
    /// </summary>
    public static string WithoutParameters(string text)
    {
        var open = text.Length > 0 && text[^1] is ')' or ']' ? MatchingOpen(text) : -1;
        return open < 0 ? text : text[..open];
    }

    /// <summary>
    /// Appends a member name to the name of its container.
    /// </summary>
    public SymbolName Append(SymbolName member) =>
        new([.. Segments, .. member.Segments], member.Parameters);

    private static bool SegmentSelects(string requested, string declared, bool exact) =>
        string.Equals(requested, declared, StringComparison.Ordinal)
        || (
            !exact
            && !requested.Contains('`', StringComparison.Ordinal)
            && string.Equals(requested, BaseName(declared), StringComparison.Ordinal)
        );

    private static bool ParametersSelect(string requested, string? declared, bool exact)
    {
        if (exact || declared is null)
        {
            return string.Equals(requested, declared, StringComparison.Ordinal);
        }

        var requestedTypes = SplitAtTopLevel(requested[1..^1], ',').ToArray();
        var declaredTypes = SplitAtTopLevel(declared[1..^1], ',').ToArray();
        return requestedTypes.Length == declaredTypes.Length
            && requestedTypes
                .Zip(declaredTypes)
                .All(pair =>
                    string.Equals(pair.First, pair.Second, StringComparison.Ordinal)
                    || string.Equals(pair.First + "?", pair.Second, StringComparison.Ordinal)
                );
    }

    private static string Normalize(string parameters) =>
        string.Concat(parameters.Where(character => !char.IsWhiteSpace(character)));

    private static string WithArity(string segment)
    {
        var open = segment.IndexOf('<', StringComparison.Ordinal);
        if (open <= 0 || segment[^1] != '>')
        {
            return segment;
        }

        var arity = SplitAtTopLevel(segment[(open + 1)..^1], ',').Count();
        return $"{segment[..open]}`{arity.ToString(CultureInfo.InvariantCulture)}";
    }

    private static int MatchingOpen(string text)
    {
        var depth = 0;
        for (var index = text.Length - 1; index >= 0; index--)
        {
            depth -= Depth(text, index);
            if (depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexAtTopLevel(string text, string value)
    {
        var depth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            depth += Depth(text, index);
            if (depth == 0 && string.CompareOrdinal(text, index, value, 0, value.Length) == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitAtTopLevel(string text, char separator)
    {
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            depth += Depth(text, index);
            if (depth == 0 && text[index] == separator)
            {
                yield return text[start..index];
                start = index + 1;
            }
        }

        yield return text[start..];
    }

    private static int Depth(string text, int index)
    {
        var depth = text[index] switch
        {
            '(' or '[' or '<' => 1,
            ')' or ']' or '>' => -1,
            _ => 0,
        };
        return text[index] is '<' or '>' && IsOperatorToken(text, index) ? 0 : depth;
    }

    // Angle brackets in operator names (e.g. "operator <(Money, Money)") are not generic arguments
    private static bool IsOperatorToken(string text, int index)
    {
        var start = index;
        while (start > 0 && _operatorCharacters.Contains(text[start - 1]))
        {
            start--;
        }

        var keywordEnd = start;
        while (keywordEnd > 0 && text[keywordEnd - 1] == ' ')
        {
            keywordEnd--;
        }

        var keywordStart = keywordEnd - OperatorKeyword.Length;
        return keywordStart >= 0
            && string.CompareOrdinal(text, keywordStart, OperatorKeyword, 0, OperatorKeyword.Length)
                == 0
            && (keywordStart == 0 || !char.IsLetterOrDigit(text[keywordStart - 1]));
    }
}
