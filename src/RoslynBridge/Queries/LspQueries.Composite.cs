// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Requests;
using RoslynBridge.Symbols;

namespace RoslynBridge.Queries;

internal sealed partial class LspQueries
{
    // Leaves room for the part headers and the truncation line
    private const int MaximumSourceCharacters = ResultText.MaximumResponseCharacters - 1_000;

    /// <summary>
    /// Returns the saved text of each declaration of a symbol, with its documentation comment.
    /// Lines past the response budget are counted in omittedLines.
    /// </summary>
    public async Task<Result<JsonNode?>> SourceAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var found = await DeclarationsAsync(request, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var items = new JsonArray();
        var remaining = MaximumSourceCharacters;
        var omitted = 0;
        foreach (var part in found.Value)
        {
            var lines = SourceLines.ReadLines(part.File);
            var last = Math.Min(part.LastLine, lines.Length);
            var first = DocumentationStart(lines, part.FirstLine);
            var text = new StringBuilder();
            var line = first;
            for (; line <= last && lines[line - 1].Length < remaining; line++)
            {
                text.Append(lines[line - 1]).Append('\n');
                remaining -= lines[line - 1].Length + 1;
            }

            omitted += last - line + 1;
            if (text.Length == 0)
            {
                continue;
            }

            var item = Declaration(part);
            item["lines"] = new JsonArray(first, line - 1);
            item["text"] = text.ToString();
            items.Add((JsonNode)item);
        }

        var result = new JsonObject { ["items"] = items };
        if (omitted > 0)
        {
            result["omittedLines"] = omitted;
        }

        return result;
    }

    /// <summary>
    /// Returns the members declared in a type, across its partial declarations.
    /// </summary>
    public async Task<Result<JsonNode?>> MembersAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        var found = await DeclarationsAsync(request, cancellationToken);
        if (found.Error is { } error)
        {
            return error;
        }

        var members = await resolver.MembersAsync(found.Value, cancellationToken);
        var result = Declaration(found.Value[0]);
        result["items"] = new JsonArray([
            .. members
                .Take(request.Limit)
                .Select(member =>
                {
                    var item = Declaration(member);
                    item["symbol"] = member.Signature;
                    item["position"] = new JsonArray(member.Line, member.Character);
                    return (JsonNode)item;
                }),
        ]);
        result["total"] = members.Count;
        result["truncated"] = members.Count > request.Limit;
        return result;
    }

    // A request names the symbol, or gives a line inside its declaration
    private async Task<Result<IReadOnlyList<SymbolDeclaration>>> DeclarationsAsync(
        LspRequest request,
        CancellationToken cancellationToken
    )
    {
        if (request.Symbol is not null)
        {
            return request is { Line: null, Character: null }
                ? await resolver.ResolveAsync(request.Symbol, request.File, cancellationToken)
                : ToolError.InvalidRequest(
                    "Pass symbol with an optional file, or a file with a line."
                );
        }

        if (request is not { File: not null, Line: > 0 and var line })
        {
            return ToolError.InvalidRequest("Provide symbol, or a file with a one-based line.");
        }

        var declaration = await resolver.DeclarationAtAsync(
            paths.Resolve(request.File),
            line,
            cancellationToken
        );
        return declaration is null
            ? ToolError.InvalidRequest($"No declaration contains line {line}.")
            : new[] { declaration };
    }

    private JsonObject Declaration(SymbolDeclaration declaration)
    {
        var item = new JsonObject
        {
            ["symbol"] = declaration.Name,
            ["file"] = paths.RelativePath(declaration.File),
        };
        if (declaration.Kind is { } kind && LspSymbolKinds.TryGetName(kind, out var name))
        {
            item["kind"] = name;
        }

        return item;
    }

    // Documentation comments are trivia, so the declaration range starts after them
    private static int DocumentationStart(string[] lines, int firstLine)
    {
        var line = firstLine;
        while (
            line > 1
            && lines[line - 2].AsSpan().TrimStart().StartsWith("///", StringComparison.Ordinal)
        )
        {
            line--;
        }

        return line;
    }
}
