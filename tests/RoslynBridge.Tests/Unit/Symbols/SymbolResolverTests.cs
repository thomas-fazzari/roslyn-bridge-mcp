// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json.Nodes;
using RoslynBridge.Symbols;
using static RoslynBridge.Tests.Fakes.LspSymbolJson;

namespace RoslynBridge.Tests.Unit.Symbols;

public sealed class SymbolResolverTests
{
    private const string File = "/workspace/Shapes.cs";

    // Document symbols in the shape Roslyn returns them
    private static readonly JsonArray _documentSymbols = new(
        DocumentSymbol(
            "Sample.Application",
            3,
            0,
            DocumentSymbol(
                "Box<T>",
                5,
                2,
                DocumentSymbol("Box(T)", 6, 4),
                DocumentSymbol("Value : T", 7, 6),
                DocumentSymbol("Put(int) : void", 6, 8),
                DocumentSymbol("Put(string?) : void", 6, 10),
                DocumentSymbol("Inner", 5, 12, DocumentSymbol("Run() : void", 6, 14)),
                DocumentSymbol("operator <(Box<T>, Box<T>) : bool", 25, 16),
                DocumentSymbol("Put(int", 6, 18)
            ),
            DocumentSymbol("Box", 5, 20, DocumentSymbol("Put(int) : void", 6, 22)),
            DocumentSymbol(
                "Extensions",
                5,
                24,
                DocumentSymbol("extension(Box)", 5, 26, DocumentSymbol("Count() : int", 6, 28)),
                DocumentSymbol(
                    "extension<T>(Box<T>)",
                    5,
                    34,
                    DocumentSymbol("Twice() : int", 6, 36)
                ),
                DocumentSymbol("Run() : void", 6, 30, DocumentSymbol("Step(int) : void", 6, 32))
            )
        )
    );

    [Theory]
    [InlineData(
        "Box<T>.Put",
        new[] { "Sample.Application.Box<T>.Put(int)", "Sample.Application.Box<T>.Put(string?)" }
    )]
    [InlineData("Box.Put", new[] { "Sample.Application.Box.Put(int)" })]
    [InlineData("Put(string)", new[] { "Sample.Application.Box<T>.Put(string?)" })]
    [InlineData("Box(T)", new[] { "Sample.Application.Box<T>.Box(T)" })]
    [InlineData("Box.Inner.Run", new[] { "Sample.Application.Box<T>.Inner.Run()" })]
    [InlineData("operator <", new[] { "Sample.Application.Box<T>.operator <(Box<T>, Box<T>)" })]
    [InlineData("Extensions.Count", new[] { "Sample.Application.Extensions.Count()" })]
    [InlineData("Extensions.Twice", new[] { "Sample.Application.Extensions.Twice()" })]
    [InlineData("Run.Step", new[] { "Sample.Application.Extensions.Run.Step(int)" })]
    [InlineData("Application", new string[0])]
    public void FindsDeclarationsByQualifiedName(string requested, string[] expected) =>
        Find(requested).Select(declaration => declaration.Name).Should().Equal(expected);

    [Fact]
    public void ReturnsOneBasedPositionOfTheDeclaredName()
    {
        var declaration = Find("Box.Value").Should().ContainSingle().Which;

        declaration.File.Should().Be(File);
        (declaration.Line, declaration.Character).Should().Be((7, 5));
    }

    [Fact]
    public void EveryDeclaredNameResolvesBackToItsDeclaration()
    {
        string[] names =
        [
            "Box",
            "Box<T>",
            "Box(T)",
            "Value",
            "Put",
            "Inner",
            "Run",
            "operator <",
            "Extensions",
            "Count",
            "Step",
        ];
        var declarations = names.SelectMany(Find).Distinct().ToArray();
        declarations.Should().HaveCount(14);

        foreach (var declaration in declarations)
        {
            Find(declaration.Name).Should().ContainSingle().Which.Should().Be(declaration);
        }
    }

    [Theory]
    [InlineData("app", 1)]
    [InlineData("lib", 2)]
    [InlineData(null, 1)]
    public void SameNamedDeclarationsAreOneSymbolOnlyWithinAProject(
        string? secondProject,
        int expected
    )
    {
        var declarations = new[]
        {
            new SymbolDeclaration("Program", "Program", 5, "/workspace/App/Program.cs", 1, 1, 1, 9),
            new SymbolDeclaration("Program", "Program", 5, "/workspace/Lib/Program.cs", 1, 1, 1, 9),
        };
        var projects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["/workspace/App/Program.cs"] = ["app"],
            ["/workspace/Lib/Program.cs"] = secondProject is null ? [] : [secondProject],
        };

        var symbols = SymbolResolver.DistinctSymbols(declarations, projects);

        symbols.Should().HaveCount(expected);
    }

    private static IEnumerable<SymbolDeclaration> Find(string requested) =>
        SymbolResolver.FindDeclarations([(File, _documentSymbols)], SymbolName.Parse(requested)!);
}
