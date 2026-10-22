// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Symbols;

namespace RoslynBridge.Tests.Unit.Symbols;

public sealed class SymbolNameTests
{
    [Theory]
    [InlineData("Put(int) : void", new[] { "Put" }, "(int)")]
    [InlineData("Value : T", new[] { "Value" }, null)]
    [InlineData("this[int] : string", new[] { "this" }, "[int]")]
    [InlineData("Map<TResult>(Func<T, TResult>) : TResult", new[] { "Map`1" }, "(Func<T,TResult>)")]
    [InlineData("Box<T>", new[] { "Box`1" }, null)]
    [InlineData("Pair<TKey, TValue>", new[] { "Pair`2" }, null)]
    [InlineData("Sample.Application", new[] { "Sample", "Application" }, null)]
    [InlineData("Sample.Box<T>.Put( int )", new[] { "Sample", "Box`1", "Put" }, "(int)")]
    [InlineData("operator <(Money, Money) : bool", new[] { "operator <" }, "(Money,Money)")]
    [InlineData("operator >>(Money, int) : Money", new[] { "operator >>" }, "(Money,int)")]
    [InlineData(
        "Money.operator <=(Money, Money)",
        new[] { "Money", "operator <=" },
        "(Money,Money)"
    )]
    [InlineData(
        "implicit operator decimal(Money) : decimal",
        new[] { "implicit operator decimal" },
        "(Money)"
    )]
    public void ParsesRoslynAndRequestedNames(string text, string[] segments, string? parameters)
    {
        var name = SymbolName.Parse(text)!;

        name.Segments.Should().Equal(segments);
        name.Parameters.Should().Be(parameters);
    }

    [Theory]
    [InlineData("Put", true, true)]
    [InlineData("Box.Put", false, true)]
    [InlineData("Sample.Application.Box.Put", false, true)]
    [InlineData("Box<T>.Put(string?)", true, true)]
    [InlineData("Box<U>.Put(string)", false, true)]
    [InlineData("Box<T, U>.Put", false, false)]
    [InlineData("Box.Put(int)", false, false)]
    [InlineData("ox.Put", false, false)]
    [InlineData("Other.Box.Put", false, false)]
    public void SelectsExactlyOrLooselyByTrailingSegmentsAndParameters(
        string requested,
        bool exact,
        bool loose
    )
    {
        var declared = SymbolName.Parse("Sample.Application.Box<T>.Put(string?)")!;
        var name = SymbolName.Parse(requested)!;

        name.Selects(declared, exact: true).Should().Be(exact);
        name.Selects(declared, exact: false).Should().Be(loose);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Box..Put")]
    [InlineData("Put(int")]
    public void RejectsInvalidNames(string text) => SymbolName.Parse(text).Should().BeNull();
}
