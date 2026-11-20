// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Features.Navigate;
using RoslynBridge.Features.Symbols;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Results;
using RoslynBridge.Symbols;
using RoslynBridge.Tests.Fakes;
using RoslynBridge.Workspace;
using static RoslynBridge.Tests.Fakes.LspLocationJson;

namespace RoslynBridge.Tests.Unit.Symbols;

public sealed class LspResultsExtensionsTests
{
    private static readonly WorkspacePaths _paths = new(
        Path.Combine(Path.GetTempPath(), "queries")
    );

    [Fact]
    public void GroupsSortsAndDeduplicatesLocationsWithOneBasedCoordinates()
    {
        var locations = new JsonArray(
            Location("Second.cs", 10, 2),
            Location("First.cs", 20, 8),
            Location("First.cs", 0, 4),
            Location("First.cs", 20, 8)
        );

        var result = ReadLocations(locations)
            .ToLocations(_paths, Paging.Default, skipGenerated: false)
            .Succeeded();

        result.Total.Should().Be(3);
        result.NextOffset.Should().BeNull();
        result.Files.Should().HaveCount(2);
        result.Files[0].File.Text.Should().Be("First.cs");
        result
            .Files[0]
            .Positions.Should()
            .Equal(new SourcePosition(1, 5), new SourcePosition(21, 9));
        result.Files[1].File.Text.Should().Be("Second.cs");
        result.Files[1].Positions.Should().Equal(new SourcePosition(11, 3));
        locations.Should().HaveCount(4);
        locations[0]!["range"].Should().NotBeNull();
    }

    [Fact]
    public void LimitsOccurrencesBeforeGroupingAndPreservesTotal()
    {
        var locations = new JsonArray(
            Location("First.cs", 0, 0),
            Location("First.cs", 1, 0),
            Location("Second.cs", 2, 0)
        );

        var result = ReadLocations(locations)
            .ToLocations(_paths, new Paging(2, 0), skipGenerated: false)
            .Succeeded();

        result.Total.Should().Be(3);
        result.NextOffset.Should().Be(2);
        result.Files.Should().ContainSingle().Which.Positions.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsSingleLocationsAndLocationLinks(bool link)
    {
        var location = Location("Source.cs", 4, 8);
        if (link)
        {
            location = new JsonObject
            {
                ["targetUri"] = location["uri"]!.DeepClone(),
                ["targetRange"] = Range(0, 0),
                ["targetSelectionRange"] = location["range"]!.DeepClone(),
                ["originSelectionRange"] = Range(1, 1),
            };
        }

        var result = ReadLocations([location])
            .ToLocations(_paths, Paging.Default, skipGenerated: false)
            .Succeeded();

        result.Total.Should().Be(1);
        result
            .Files.Should()
            .ContainSingle()
            .Which.Positions.Should()
            .Equal(new SourcePosition(5, 9));
    }

    [Theory]
    [InlineData("roslyn-source-generated://assembly/Source.cs")]
    [InlineData("file:///outside/Source.cs")]
    public void PreservesUrisOutsideTheWorkspace(string value)
    {
        var location = new JsonObject { ["uri"] = value, ["range"] = Range(0, 0) };

        var result = ReadLocations([location])
            .ToLocations(_paths, Paging.Default, skipGenerated: false)
            .Succeeded();

        result
            .Files.Should()
            .ContainSingle()
            .Which.File.Should()
            .Be(new LocationFile.External(value));
    }

    [Fact]
    public void SkipsSourceGeneratedLocationsAndCountsThem()
    {
        var locations = new JsonArray(
            Location("Source.cs", 0, 0),
            new JsonObject
            {
                ["uri"] = "roslyn-source-generated://assembly/Generated.cs",
                ["range"] = Range(0, 0),
            }
        );

        var result = ReadLocations(locations)
            .ToLocations(_paths, Paging.Default, skipGenerated: true)
            .Succeeded();

        result.Total.Should().Be(1);
        result.Generated.Should().Be(1);
        result.Files.Should().ContainSingle();
    }

    [Fact]
    public void CompactsDiagnosticsToOneBasedStartAndNamedSeverity()
    {
        var diagnostic = new JsonObject
        {
            ["range"] = Range(3, 7),
            ["severity"] = 1,
            ["code"] = "CS0246",
            ["codeDescription"] = new JsonObject { ["href"] = "https://example.com/CS0246" },
            ["message"] = "The type could not be found.",
            ["tags"] = new JsonArray(1),
        };

        var result = diagnostic.Read<LspDiagnostic>().Succeeded().ToDiagnostic();

        result
            .Should()
            .Be(
                new Diagnostic(
                    new SourcePosition(4, 8),
                    DiagnosticSeverity.Error,
                    "CS0246",
                    "The type could not be found."
                )
            );
    }

    [Fact]
    public void CompactsNestedDocumentSymbols()
    {
        var symbols = new JsonArray(
            new JsonObject
            {
                ["name"] = "IGreeter",
                ["detail"] = "IGreeter",
                ["kind"] = 11,
                ["range"] = Range(2, 0),
                ["selectionRange"] = Range(2, 17),
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "Greet(string) : string",
                        ["kind"] = 6,
                        ["range"] = Range(4, 4),
                        ["selectionRange"] = Range(4, 11),
                        ["children"] = new JsonArray(),
                    }
                ),
            }
        );

        var result = SymbolList.From(ReadSymbols(symbols), _paths, Paging.Default);

        result
            .Items.Should()
            .BeEquivalentTo([
                new SymbolItem("IGreeter", "interface")
                {
                    Position = new(3, 18),
                    End = new(3, 2),
                    Children =
                    [
                        new SymbolItem("Greet(string) : string", "method")
                        {
                            Position = new(5, 12),
                            End = new(5, 6),
                        },
                    ],
                },
            ]);
    }

    [Fact]
    public void LimitsNestedDocumentSymbolsAndCountsThemAll()
    {
        var symbols = new JsonArray(
            new JsonObject
            {
                ["name"] = "Sample",
                ["kind"] = 3,
                ["range"] = Range(0, 0),
                ["selectionRange"] = Range(0, 10),
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "First",
                        ["kind"] = 5,
                        ["range"] = Range(2, 0),
                        ["selectionRange"] = Range(2, 13),
                    },
                    new JsonObject
                    {
                        ["name"] = "Second",
                        ["kind"] = 5,
                        ["range"] = Range(4, 0),
                        ["selectionRange"] = Range(4, 13),
                    }
                ),
            }
        );

        var result = SymbolList.From(ReadSymbols(symbols), _paths, new Paging(2, 0));

        result.Total.Should().Be(3);
        result.NextOffset.Should().Be(2);
        result.Items[0].Children.Select(child => child.Name).Should().Equal("First");
    }

    [Fact]
    public void SymbolOffsetSkipsInDocumentOrderAndKeepsTheMembersOfASkippedSymbol()
    {
        var symbols = new JsonArray(
            new JsonObject
            {
                ["name"] = "Sample",
                ["kind"] = 3,
                ["range"] = Range(0, 0),
                ["selectionRange"] = Range(0, 10),
                ["children"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "First",
                        ["kind"] = 5,
                        ["range"] = Range(2, 0),
                        ["selectionRange"] = Range(2, 13),
                    },
                    new JsonObject
                    {
                        ["name"] = "Second",
                        ["kind"] = 5,
                        ["range"] = Range(4, 0),
                        ["selectionRange"] = Range(4, 13),
                    }
                ),
            }
        );

        var first = SymbolList.From(ReadSymbols(symbols), _paths, new Paging(1, 1));
        var last = SymbolList.From(ReadSymbols(symbols), _paths, new Paging(2, 2));

        first.Total.Should().Be(3);
        first.Items.Select(item => item.Name).Should().Equal("First");
        first.NextOffset.Should().Be(2);
        last.Items.Select(item => item.Name).Should().Equal("Second");
        last.NextOffset.Should().BeNull();
    }

    [Fact]
    public void CompactsWorkspaceSymbolsWithRelativeFilesAndLimit()
    {
        var symbols = new JsonArray(
            new JsonObject
            {
                ["name"] = "Greeter",
                ["kind"] = 5,
                ["location"] = Location("Application/Greeter.cs", 5, 21),
                ["containerName"] = "in Application",
            },
            new JsonObject { ["name"] = "Other", ["kind"] = 5 }
        );

        var result = SymbolList.From(ReadSymbols(symbols), _paths, new Paging(1, 0));

        result.Total.Should().Be(2);
        result.NextOffset.Should().Be(1);
        result
            .Items.Should()
            .BeEquivalentTo([
                new SymbolItem("Greeter", "class")
                {
                    Container = "Application",
                    File = new LocationFile.Workspace(
                        TestHelpers.Resolved(_paths.Root, "Application/Greeter.cs")
                    ),
                    Position = new(6, 22),
                },
            ]);
    }

    [Fact]
    public void CompactsIncomingCallsWithCallSites()
    {
        var calls = new JsonArray(
            new JsonObject
            {
                ["from"] = new JsonObject
                {
                    ["name"] = "Consumer.Run(IGreeter)",
                    ["kind"] = 6,
                    ["uri"] = Location("Consumer.cs", 0, 0)["uri"]!.DeepClone(),
                    ["range"] = Range(6, 4),
                    ["selectionRange"] = Range(6, 25),
                },
                ["fromRanges"] = new JsonArray(Range(6, 58), Range(7, 2)),
            }
        );

        var result = ReadCalls(calls).ToRelatedSymbols(Paging.Default);

        var item = result.Items.Should().ContainSingle().Which;
        item.File!.Text.Should().Be("Consumer.cs");
        item.Position.Should().Be(new SourcePosition(7, 26));
        item.Calls.Should().Equal(new SourcePosition(7, 59), new SourcePosition(8, 3));
    }

    [Fact]
    public void RelatedSymbolsAreOrderedByFileAndPositionBeforePaging()
    {
        var calls = new JsonArray(
            IncomingCall("Consumer.cs", 9),
            IncomingCall("Another.cs", 20),
            IncomingCall("Consumer.cs", 2)
        );

        var all = ReadCalls(calls).ToRelatedSymbols(Paging.Default);
        var page = ReadCalls(calls).ToRelatedSymbols(new Paging(1, 1));

        all.Items.Select(item => (item.File!.Text, item.Position!.Value.Line))
            .Should()
            .Equal(("Another.cs", 21), ("Consumer.cs", 3), ("Consumer.cs", 10));
        page.Items.Should().ContainSingle().Which.File!.Text.Should().Be("Consumer.cs");
        page.Items[0].Position!.Value.Line.Should().Be(3);
        page.NextOffset.Should().Be(2);
    }

    private static LspLocation[] ReadLocations(JsonArray locations) =>
        locations.Read<LspLocation[]>().Succeeded();

    private static LspSymbol[] ReadSymbols(JsonArray symbols) =>
        symbols.Read<LspSymbol[]>().Succeeded();

    private static List<SymbolItem> ReadCalls(JsonArray calls) =>
        calls.Read<LspCall[]>().Succeeded().ToRelatedItems(_paths).Succeeded();

    private static JsonObject Location(string file, int line, int character) =>
        LspLocationJson.Location(Path.Combine(_paths.Root, file), line, character);

    private static JsonObject IncomingCall(string file, int line) =>
        new()
        {
            ["from"] = new JsonObject
            {
                ["name"] = string.Create(CultureInfo.InvariantCulture, $"Caller{line}"),
                ["kind"] = 6,
                ["uri"] = Location(file, 0, 0)["uri"]!.DeepClone(),
                ["range"] = Range(line, 4),
                ["selectionRange"] = Range(line, 4),
            },
            ["fromRanges"] = new JsonArray(Range(line, 8)),
        };
}
