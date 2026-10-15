// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Text.Json.Nodes;
using RoslynBridge.Symbols;

namespace RoslynBridge.Tests.Unit.Symbols;

public sealed class SymbolResolverTests
{
    private const string File = "/workspace/Shapes.cs";

    // Document symbols in the shape Roslyn returns them
    private static readonly JsonArray _documentSymbols = new(
        Symbol(
            "Sample.Application",
            3,
            0,
            Symbol(
                "Box<T>",
                5,
                2,
                Symbol("Box(T)", 6, 4),
                Symbol("Value : T", 7, 6),
                Symbol("Put(int) : void", 6, 8),
                Symbol("Put(string?) : void", 6, 10),
                Symbol("Inner", 5, 12, Symbol("Run() : void", 6, 14)),
                Symbol("operator <(Box<T>, Box<T>) : bool", 25, 16),
                Symbol("Put(int", 6, 18)
            ),
            Symbol("Box", 5, 20, Symbol("Put(int) : void", 6, 22)),
            Symbol(
                "Extensions",
                5,
                24,
                Symbol("extension(Box)", 5, 26, Symbol("Count() : int", 6, 28)),
                Symbol("Run() : void", 6, 30, Symbol("Step(int) : void", 6, 32))
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

    private static JsonObject Symbol(string name, int kind, int line, params JsonObject[] children)
    {
        var position = new JsonObject { ["line"] = line, ["character"] = 4 };
        return new JsonObject
        {
            ["name"] = name,
            ["kind"] = kind,
            ["range"] = new JsonObject
            {
                ["start"] = position.DeepClone(),
                ["end"] = position.DeepClone(),
            },
            ["selectionRange"] = new JsonObject
            {
                ["start"] = position,
                ["end"] = position.DeepClone(),
            },
            ["children"] = new JsonArray([.. children]),
        };
    }
}
