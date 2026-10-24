// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.Json.Nodes;
using RoslynBridge.Changes;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using static RoslynBridge.Tests.Assertions.ResultLines;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Integration.Roslyn;

/// <summary>
/// Tests the MCP bridge against the real Roslyn language server in temporary workspaces.
/// These tests are explicit and are excluded from mise run test and mise run check.
/// </summary>
/// <remarks>
/// Requires roslyn-language-server on PATH, or its path in ROSLYN_BRIDGE_TEST_SERVER.
/// Set it locally in the [env] table of mise.local.toml. From the repository root, run:
/// <code>
/// mise run test-integration
/// </code>
/// The command builds the project. The tests restore their fixtures, launch Roslyn,
/// and delete their temporary workspaces when finished.
/// To test a published native bridge, set its absolute executable path:
/// <code>
/// ROSLYN_BRIDGE_TEST_EXECUTABLE=/absolute/path/RoslynBridge mise run test-integration
/// </code>
/// </remarks>
public sealed partial class RoslynMcpTests
{
    private const int TestTimeoutMilliseconds = 300_000;

    private const string ApplyFromPreview = "preview";
    private const string ApplyFromListing = "listing";
    private const string ApplyWithoutProposal = "direct";
    private const string LoadingLine = "loading: true";
    private const string IdleLine = "loading: false";
    private const string ProgressLine = "progress:";

    private const string ContractTestFile = "Contracts/IGreeter.cs";
    private const string ImplementationTestFile = "Application/Greeter.cs";
    private const string ConsumerTestFile = "Application/Consumer.cs";
    private const string OtherGreeterTestFile = "Application/OtherGreeter.cs";
    private const string ProblemsTestFile = "Application/Problems.cs";
    private const string ApplicationProgramTestFile = "Application/Program.cs";
    private const string ContractsProgramTestFile = "Contracts/Program.cs";
    private const string ProgramFileName = "Program.cs";
    private const string TotalTestSymbol = "Counter.Total";
    private const string StepTestSymbol = "Counter.Step";

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ReportsFreshDiagnosticsAndNavigatesAcrossProjectsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var capabilities = JsonNode.Parse(
            await workspace.CallAsync(new LspRequest { Action = LspAction.Capabilities })
        )!;
        capabilities["definitionProvider"]!.GetValue<bool>().Should().BeTrue();
        capabilities["typeDefinitionProvider"]!.GetValue<bool>().Should().BeTrue();

        var diagnostics = await DiagnosticsAsync(workspace);
        diagnostics.Should().Contain(new Diagnostic("error", "CS0246"));
        diagnostics.Should().Contain(new Diagnostic("warning", "CS8603"));

        var definitionRequest = await workspace.AtAsync(
            LspAction.Definition,
            ConsumerTestFile,
            "Greet("
        );
        var definition = await workspace.CallAsync(definitionRequest);
        definition.Should().HaveSingleLocation(ContractTestFile);
        var target = await workspace.AtAsync(LspAction.Definition, ContractTestFile, "Greet(");
        definition
            .Should()
            .Be(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ContractTestFile} {target.Line}:{target.Character}"
                )
            );
        var raw = JsonNode.Parse(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.Request,
                    Method = LspMethods.TextDocumentDefinition,
                    Parameters = new JsonObject
                    {
                        ["textDocument"] = new JsonObject
                        {
                            ["uri"] = new Uri(workspace.FilePath(ConsumerTestFile)).AbsoluteUri,
                        },
                        ["position"] = new JsonObject
                        {
                            ["line"] = definitionRequest.Line - 1,
                            ["character"] = definitionRequest.Character - 1,
                        },
                    },
                }
            )
        )!;
        var rawLocation = raw.AsArray().Should().ContainSingle().Which!;
        var rawUri = (rawLocation["uri"] ?? rawLocation["targetUri"])!.GetValue<string>();
        new WorkspacePaths(workspace.FilePath("."))
            .TryGetWorkspaceFile(rawUri)
            .Should()
            .Be(Path.GetFullPath(workspace.FilePath(ContractTestFile)));
        var rawStart = (rawLocation["range"] ?? rawLocation["targetSelectionRange"])!["start"]!;
        rawStart["line"]!.GetValue<int>().Should().Be(target.Line - 1);
        rawStart["character"]!.GetValue<int>().Should().Be(target.Character - 1);

        var typeDefinition = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.TypeDefinition, ConsumerTestFile, "greeter.Greet")
        );
        typeDefinition.Should().HaveSingleLocation(ContractTestFile);

        var implementation = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Implementation, ContractTestFile, "Greet(")
        );
        implementation.Should().HaveSingleLocation(ImplementationTestFile);

        var references = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.References, ContractTestFile, "Greet(")
        );
        Locations(references).Should().Contain(ConsumerTestFile);
        Locations(references).Should().NotContain(OtherGreeterTestFile);

        var hover = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Hover, ConsumerTestFile, "Greet(")
        );
        hover.Should().Contain("Greet").And.NotContain("```");

        var callers = Lines(
            await workspace.CallAsync(
                await workspace.AtAsync(LspAction.Callers, ContractTestFile, "Greet(")
            )
        );
        callers.Should().HaveCount(2);
        callers[0].Should().StartWith(ConsumerTestFile + ":");
        callers[1].Should().MatchRegex(@"^  calls \d+:\d+$");

        // A returned symbol name resolves back to its declaration
        var resolved = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = SymbolName(callers[0]) }
        );
        resolved.Should().HaveSingleLocation(ConsumerTestFile);

        var withContext = Lines(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.References,
                    Symbol = "IGreeter.Greet",
                    Context = true,
                }
            )
        );
        var consumer = Array.IndexOf(withContext, ConsumerTestFile);
        consumer.Should().BeGreaterThanOrEqualTo(0);
        withContext[consumer + 1].Should().Contain("greeter.Greet(");

        var subtypes = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Subtypes, ContractTestFile, "IGreeter")
        );
        Lines(subtypes).Select(FileOf).Should().Equal(ImplementationTestFile);

        var symbols = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, Query = "IGreeter" }
        );
        Lines(symbols).Should().Contain(line => line.Contains(" interface IGreeter"));

        var documentSymbols = Lines(
                await workspace.CallAsync(
                    new LspRequest { Action = LspAction.Symbols, File = ContractTestFile }
                )
            )
            .Select(SymbolName)
            .ToArray();
        documentSymbols.Should().Contain("IGreeter");
        documentSymbols
            .Should()
            .Contain(name => name.StartsWith("Greet(", StringComparison.Ordinal));

        var text = await workspace.ReadFileAsync(ProblemsTestFile);
        await workspace.WriteFileAsync(
            ProblemsTestFile,
            "using System.Collections.Generic;\n" + text
        );

        var refreshed = await DiagnosticsAsync(workspace);
        refreshed.Should().NotContain(item => item.Code == "CS0246");
        refreshed.Should().Contain(item => item.Code == "CS8603");

        (await workspace.StopAsync()).Should().Be(0);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task DiagnosticsOmitCleanFilesAndKeepCountsWhenTruncatedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        const string cleanFile = "Contracts/Clean.cs";
        await workspace.WriteFileAsync(cleanFile, string.Empty);
        var clean = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = cleanFile }
        );
        clean.Should().Be("1 file checked, no diagnostics");

        var request = new LspRequest
        {
            Action = LspAction.Diagnostics,
            File = "Application/*.cs",
            Limit = LspRequest.MaximumResultLimit,
        };
        var full = await workspace.CallAsync(request);
        var limited = await workspace.CallAsync(request with { Limit = 1 });
        var fullSummary = Summary(full);

        fullSummary.FilesChecked.Should().Be(6);
        full.Should().NotContain("truncated:");
        fullSummary.FilesWithDiagnostics.Should().Be(Lines(full).Count(IsFileLine));
        Summary(limited).Should().Be(fullSummary);

        var withSuggestions = await workspace.CallAsync(
            request with
            {
                Severity = DiagnosticSeverity.Hint,
            }
        );
        Diagnostics(full)
            .Should()
            .OnlyContain(item => item.Severity == "error" || item.Severity == "warning");
        Summary(withSuggestions).Total.Should().Be(fullSummary.Total + fullSummary.BelowSeverity);

        limited.Should().Contain("truncated:").And.NotContain("incomplete:");
        Lines(limited).Count(IsFileLine).Should().Be(1);
        Diagnostics(limited).Should().ContainSingle();
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ImmediatelyReportsSemanticDiagnosticsForANewFileAsync()
    {
        const string newFile = "Application/NewFile.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);

        // Roslyn first treats a new file as a miscellaneous file, without semantic diagnostics
        await workspace.WriteFileAsync(
            newFile,
            "namespace Sample.Application;\n\npublic static class NewFile\n{\n    public static int Wrong() => \"text\";\n}\n"
        );
        var result = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = newFile }
        );

        result.Should().NotContain("incomplete:");
        Diagnostics(result).Should().Contain(item => item.Code == "CS0029");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task SkipsOversizedDocumentsDuringSynchronizationAsync()
    {
        const string bigFile = "Application/Big.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var rename = await workspace.AtAsync(LspAction.Rename, ContractTestFile, "Greet(");
        await StartRoslynAsync(workspace);

        // The watcher reports the file, so synchronization tries to open it
        await workspace.WriteFileAsync(
            bigFile,
            "// " + new string('x', 1_100_000) + "\nnamespace Sample.Application;\n"
        );

        var preview = await workspace.CallAsync(rename with { NewName = "Welcome" });
        PreviewFiles(preview).Should().Contain(ConsumerTestFile);

        var diagnostics = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = ConsumerTestFile }
        );
        diagnostics.Should().NotContain("incomplete:");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ResolvesSymbolsInFilesDeclaringComparisonOperatorsAsync()
    {
        const string moneyFile = "Application/Money.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            moneyFile,
            "namespace Sample.Application;\n\npublic readonly record struct Money(decimal Amount)\n{\n"
                + "    public Money Add(Money other) => new(Amount + other.Amount);\n"
                + "    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;\n"
                + "    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = moneyFile }
        );

        var definition = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Money.Add" }
        );

        Locations(definition).Should().Equal(moneyFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ReadsSourceAndMembersAcrossPartialDeclarationsAsync()
    {
        const string ledgerFile = "Application/Ledger.cs";
        const string totalsFile = "Application/Ledger.Totals.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            ledgerFile,
            "namespace Sample.Application;\n\npublic partial class Ledger\n{\n"
                + "    /// <summary>Adds an amount.</summary>\n"
                + "    public void Add(decimal amount) => Total += amount;\n}\n"
        );
        await workspace.WriteFileAsync(
            totalsFile,
            "namespace Sample.Application;\n\npublic partial class Ledger\n{\n"
                + "    public decimal Total { get; private set; }\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = "Application/Ledger*.cs" }
        );

        var members = Lines(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Members, Symbol = "Ledger" }
            )
        );
        members[0].Should().Be("class Sample.Application.Ledger");
        members.Should().Contain([ledgerFile, totalsFile]);
        members
            .Should()
            .Contain(line =>
                line.EndsWith(" method Add(decimal) : void", StringComparison.Ordinal)
            );
        members
            .Should()
            .Contain(line => line.EndsWith(" property Total : decimal", StringComparison.Ordinal));

        var source = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Source, Symbol = "Ledger.Add" }
        );
        Lines(source)[0]
            .Should()
            .Be($"{ledgerFile}:5-6 method Sample.Application.Ledger.Add(decimal)");
        source
            .Should()
            .Contain("/// <summary>Adds an amount.</summary>")
            .And.Contain("Total += amount;");

        var parts = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Source, Symbol = "Ledger" }
        );
        Lines(parts)
            .Where(line =>
                line.Contains(" class Sample.Application.Ledger", StringComparison.Ordinal)
            )
            .Select(line => line[..line.IndexOf(':', StringComparison.Ordinal)])
            .Should()
            .BeEquivalentTo([ledgerFile, totalsFile]);

        var enclosing = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Source,
                File = ConsumerTestFile,
                Line = 7,
            }
        );
        Lines(enclosing)[0]
            .Should()
            .StartWith($"{ConsumerTestFile}:7-7 method Sample.Application.Consumer.Run(");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task GathersTheImpactOfAMemberInOneCallAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var impact = Lines(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Impact, Symbol = "IGreeter.Greet" }
            )
        );

        Section(impact, "references").Select(FileOf).Should().Contain(ConsumerTestFile);
        Section(impact, "callers")
            .Should()
            .ContainSingle(line =>
                line.StartsWith(ConsumerTestFile + ":", StringComparison.Ordinal)
            );
        Section(impact, "implementation").Select(FileOf).Should().Contain(ImplementationTestFile);
        impact
            .Should()
            .NotContain(line => line.StartsWith("## subtypes", StringComparison.Ordinal));

        var typeImpact = Lines(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Impact, Symbol = "IGreeter" }
            )
        );
        Section(typeImpact, "subtypes").Select(FileOf).Should().Equal(ImplementationTestFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ListsTheReadsAndWritesOfAFieldAndAPropertyAcrossFilesAsync()
    {
        const string counterFile = "Application/Counter.cs";
        const string readerFile = "Application/CounterReader.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var totalReads = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Reads, Symbol = TotalTestSymbol }
        );
        var totalWrites = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Writes, Symbol = TotalTestSymbol }
        );
        var stepReads = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Reads, Symbol = StepTestSymbol }
        );
        var stepWrites = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Writes, Symbol = StepTestSymbol }
        );

        totalWrites
            .Should()
            .Be($"{counterFile} 11:9 12:9 13:9 14:18 15:18 19:51\n{readerFile} 7:58");
        totalReads.Should().Be($"{counterFile} 14:18 16:24 18:55 19:51\n{readerFile} 5:56");
        stepReads.Should().Be($"{counterFile} 12:18");
        stepWrites.Should().Be($"{counterFile} 17:9");

        var status = await workspace.CallAsync(new LspRequest { Action = LspAction.Status });
        status.Should().Contain(IdleLine).And.NotContain(ProgressLine);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RunsSeveralNavigationsOfOneSymbolInOneBulkCallAsync()
    {
        const string symbol = "IGreeter.Greet";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var bulk = Lines(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.Bulk,
                    Queries =
                    [
                        new LspRequest { Action = LspAction.Source, Symbol = symbol },
                        new LspRequest { Action = LspAction.References, Symbol = symbol },
                        new LspRequest { Action = LspAction.Callers, Symbol = symbol },
                        new LspRequest { Action = LspAction.Impact, Symbol = symbol },
                    ],
                }
            )
        );

        bulk.Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Should()
            .Equal(
                $"## 1 source {symbol}",
                $"## 2 references {symbol}",
                $"## 3 callers {symbol}",
                $"## 4 impact {symbol}"
            );
        bulk.Should().NotContain(line => line.StartsWith("error ", StringComparison.Ordinal));
        Section(bulk, $"1 source {symbol}").Should().Contain(line => line.Contains("string Greet"));
        Section(bulk, $"2 references {symbol}").Select(FileOf).Should().Contain(ConsumerTestFile);
        Section(bulk, $"3 callers {symbol}")
            .Should()
            .ContainSingle(line =>
                line.StartsWith(ConsumerTestFile + ":", StringComparison.Ordinal)
            );
        bulk.Should().Contain("### references").And.Contain("### callers");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task StatusReportsTheLoadProgressWhileAReloadRunsAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);

        var reload = workspace.CallAsync(new LspRequest { Action = LspAction.Reload });
        var statuses = new List<string>();
        while (!reload.IsCompleted)
        {
            statuses.Add(await workspace.CallAsync(new LspRequest { Action = LspAction.Status }));
        }

        await reload;
        var loading = statuses.Where(status =>
            status.Contains(LoadingLine, StringComparison.Ordinal)
        );
        loading.Should().NotBeEmpty("a status call during the reload sees the load");
        loading.Should().Contain(status => status.Contains(ProgressLine, StringComparison.Ordinal));
        var after = await workspace.CallAsync(new LspRequest { Action = LspAction.Status });
        after.Should().Contain(IdleLine).And.NotContain(ProgressLine);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RejectsFilesAndLinesItCannotAnalyzeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await workspace.WriteFileAsync("README.md", "# Sample\n\n[Link](https://example.com)\n");

        var markdown = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = "README.md" }
        );
        var missing = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = "Application/Missing.cs" }
        );
        var pastTheEnd = await workspace.CallRawAsync(
            new LspRequest
            {
                Action = LspAction.Hover,
                File = ConsumerTestFile,
                Line = 9_999,
                Character = 1,
            }
        );

        markdown.Should().BeError(ErrorCodes.InvalidRequest);
        missing.Should().BeError(ErrorCodes.InvalidRequest);
        pastTheEnd.Should().BeError(ErrorCodes.InvalidRequest);
        ResponseText(pastTheEnd).Should().Contain($"{ConsumerTestFile}, which has");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ResolvesExtensionBlockMembersByTheirClassAsync()
    {
        const string extensionsFile = "Application/GreetingExtensions.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            extensionsFile,
            "namespace Sample.Application;\n\npublic static class GreetingExtensions\n{\n"
                + "    extension(string name)\n    {\n"
                + "        public string Shout() => name.ToUpperInvariant();\n    }\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = extensionsFile }
        );

        var definition = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "GreetingExtensions.Shout" }
        );

        Locations(definition).Should().Equal(extensionsFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ReloadGetsTheStartupBudgetAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            requestTimeoutSeconds: 1
        );

        // The first reload starts Roslyn, the second restarts a running session
        await workspace.CallAsync(new LspRequest { Action = LspAction.Reload });

        var reload = await workspace.CallAsync(new LspRequest { Action = LspAction.Reload });

        reload.Should().StartWith("running: true\nloading: false");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ListsSameNamedTypesOfDifferentProjectsAsCandidatesAsync()
    {
        const string source =
            "namespace Sample.Application;\n\ninternal static class Program\n{\n    public static void Run() { }\n}\n";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(ApplicationProgramTestFile, source);
        await workspace.WriteFileAsync(ContractsProgramTestFile, source);
        foreach (var file in new[] { ApplicationProgramTestFile, ContractsProgramTestFile })
        {
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Diagnostics, File = file }
            );
        }

        var ambiguous = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Program.Run" }
        );
        ambiguous.Should().BeError(ErrorCodes.AmbiguousSymbol);
        Candidates(ambiguous)
            .Select(FileOf)
            .Should()
            .BeEquivalentTo([ApplicationProgramTestFile, ContractsProgramTestFile]);

        var resolved = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Definition,
                Symbol = "Program.Run",
                File = ContractsProgramTestFile,
            }
        );
        resolved.Should().HaveSingleLocation(ContractsProgramTestFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task NavigatesAndRenamesBySymbolNameAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var references = await workspace.CallAsync(
            new LspRequest { Action = LspAction.References, Symbol = "IGreeter.Greet" }
        );
        Locations(references)
            .Should()
            .Contain(ConsumerTestFile)
            .And.NotContain(OtherGreeterTestFile);

        var ambiguous = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Greet" }
        );
        ambiguous.Should().BeError(ErrorCodes.AmbiguousSymbol);
        var candidates = Candidates(ambiguous).Select(SymbolName).ToArray();
        candidates.Should().HaveCount(3);
        var contract = candidates.Should().ContainSingle(name => name.Contains("IGreeter")).Which;
        var resolved = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = contract }
        );
        resolved.Should().HaveSingleLocation(ContractTestFile);

        var preview = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                Symbol = "Consumer.Run",
                NewName = "Execute",
            }
        );
        PreviewFiles(preview).Should().Equal(ConsumerTestFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task MisspelledSymbolListsACandidateThatResolvesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var unknown = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Greter" }
        );

        unknown.Should().BeError(ErrorCodes.SymbolNotFound);
        var candidate = Candidates(unknown)
            .Select(SymbolName)
            .Should()
            .ContainSingle(name => name.EndsWith(".Greeter", StringComparison.Ordinal))
            .Which;
        var resolved = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = candidate }
        );
        resolved.Should().HaveSingleLocation(ImplementationTestFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task UnknownMemberOfAKnownTypeListsOnlyThatTypesMembersAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var misspelled = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.References, Symbol = "Counter.Totl" }
        );
        var unrelated = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.References, Symbol = "Counter.Nothing" }
        );

        misspelled.Should().BeError(ErrorCodes.SymbolNotFound);
        Candidates(misspelled)
            .Select(SymbolName)
            .Should()
            .Contain(name => name.EndsWith(".Counter.Total", StringComparison.Ordinal))
            .And.OnlyContain(name => name.Contains(".Counter.", StringComparison.Ordinal));
        unrelated.Should().BeError(ErrorCodes.SymbolNotFound);
        Candidates(unrelated).Should().BeEmpty();
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task PagesTheReferencesOfAFieldWithoutOverlapAsync()
    {
        const int pageSize = 3;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var all = ReferenceLocations(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.References, Symbol = TotalTestSymbol }
            )
        );
        var paged = new List<string>();
        var pages = 0;
        for (var offset = 0; offset >= 0; pages++)
        {
            var page = Lines(
                await workspace.CallAsync(
                    new LspRequest
                    {
                        Action = LspAction.References,
                        Symbol = TotalTestSymbol,
                        Limit = pageSize,
                        Offset = offset,
                    }
                )
            );
            paged.AddRange(ReferenceLocations(string.Join('\n', page)));
            offset = NextOffset(page);
        }

        pages.Should().BeGreaterThan(1);
        paged.Should().OnlyHaveUniqueItems().And.Equal(all);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task BulkReportsAnAmbiguousFileOnlyInTheSectionThatNamesItAsync()
    {
        const string source = "namespace Sample;\n\ninternal static class Program\n{\n}\n";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        foreach (var file in new[] { ApplicationProgramTestFile, ContractsProgramTestFile })
        {
            await workspace.WriteFileAsync(file, source);
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Diagnostics, File = file }
            );
        }

        var response = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Bulk,
                Queries =
                [
                    new LspRequest { Action = LspAction.Writes, Symbol = TotalTestSymbol },
                    new LspRequest { Action = LspAction.Reads, Symbol = TotalTestSymbol },
                    new LspRequest
                    {
                        Action = LspAction.References,
                        File = ProgramFileName,
                        Line = 3,
                        Character = 24,
                    },
                    new LspRequest { Action = LspAction.References, Symbol = "IGreeter.Greet" },
                ],
            }
        );

        var sections = response.Split("\n## ");
        sections.Should().HaveCount(4);
        sections
            .Where(section => section.Contains(ErrorCodes.AmbiguousFile, StringComparison.Ordinal))
            .Should()
            .ContainSingle(response)
            .Which.Should()
            .Contain($"references {ProgramFileName}");
        sections
            .Where(section => section.Contains("error ", StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RenamesFilesOnlyWhenRoslynAdvertisesSupportAsync()
    {
        const string destination = "Application/RenamedGreeter.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, File = ImplementationTestFile }
        );
        var request = new LspRequest
        {
            Action = LspAction.RenameFile,
            File = ImplementationTestFile,
            NewName = destination,
        };

        var capabilities = JsonNode.Parse(
            await workspace.CallAsync(new LspRequest { Action = LspAction.Capabilities })
        );
        if (capabilities?["workspace"]?["fileOperations"]?["willRename"] is null)
        {
            var original = await workspace.ReadFileAsync(ImplementationTestFile);
            var rejected = await workspace.CallRawAsync(request);
            rejected.Should().BeError(ErrorCodes.InvalidRequest);
            (await workspace.ReadFileAsync(ImplementationTestFile)).Should().Be(original);
            File.Exists(workspace.FilePath(destination)).Should().BeFalse();
            TestContext.Current.TestOutputHelper!.WriteLine(
                "Roslyn did not advertise file rename support. Refusal preserved the source."
            );
            return;
        }

        var preview = await workspace.CallAsync(request);
        File.Exists(workspace.FilePath(ImplementationTestFile)).Should().BeTrue();
        File.Exists(workspace.FilePath(destination)).Should().BeFalse();

        var applied = await workspace.CallAsync(ApplyProposal(LspAction.RenameFile, preview));
        applied.Should().BeApplied();
        File.Exists(workspace.FilePath(ImplementationTestFile)).Should().BeFalse();
        File.Exists(workspace.FilePath(destination)).Should().BeTrue();

        var implementation = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Implementation, ContractTestFile, "Greet(")
        );
        implementation.Should().HaveSingleLocation(destination);
        TestContext.Current.TestOutputHelper!.WriteLine(
            "Roslyn advertised file rename support. Preview, apply and navigation to the new path passed."
        );
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task AppliesRenameAcrossProjectsWithoutRenamingAHomonymAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var unrelatedBefore = await workspace.ReadFileAsync(OtherGreeterTestFile);
        var request = await workspace.AtAsync(LspAction.Rename, ContractTestFile, "Greet(");
        request = request with { NewName = "Welcome" };

        var preview = await workspace.CallAsync(request);
        var original = await workspace.ReadFileAsync(ContractTestFile);
        original.Should().Contain("Greet(");
        original.Should().NotContain("Welcome(");
        var hunks = Hunks(preview);
        hunks
            .Should()
            .NotBeEmpty()
            .And.AllSatisfy(hunk =>
            {
                hunk.Before.Should().Contain("Greet");
                hunk.After.Should().Contain("Welcome");
            });

        var applied = await workspace.CallAsync(ApplyProposal(LspAction.Rename, preview));
        applied.Should().BeApplied();

        foreach (var file in new[] { ContractTestFile, ImplementationTestFile, ConsumerTestFile })
        {
            var content = await workspace.ReadFileAsync(file);
            content.Should().Contain("Welcome(");
            content.Should().NotContain("Greet(");
        }

        var unrelatedAfter = await workspace.ReadFileAsync(OtherGreeterTestFile);
        unrelatedAfter.Should().Be(unrelatedBefore);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RejectsRenamePreviewAfterAnExternalEditAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = await workspace.AtAsync(LspAction.Rename, ContractTestFile, "Greet(");
        request = request with { NewName = "Welcome" };

        var preview = await workspace.CallAsync(request);
        var original = await workspace.ReadFileAsync(ConsumerTestFile);
        var changed = original.Replace("world", "friend", StringComparison.Ordinal);
        await workspace.WriteFileAsync(ConsumerTestFile, changed);

        var rejected = await workspace.CallRawAsync(ApplyProposal(LspAction.Rename, preview));
        rejected.Should().BeError(ErrorCodes.StaleEdit);
        (await workspace.ReadFileAsync(ConsumerTestFile)).Should().Be(changed);
        var contract = await workspace.ReadFileAsync(ContractTestFile);
        contract.Should().Contain("Greet(");
        contract.Should().NotContain("Welcome(");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task AppliesARenameWithoutAPreviewAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var applied = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                Symbol = "IGreeter.Greet",
                NewName = "Welcome",
                Apply = true,
            }
        );

        applied.Should().BeApplied();
        foreach (var file in new[] { ContractTestFile, ImplementationTestFile, ConsumerTestFile })
        {
            (await workspace.ReadFileAsync(file)).Should().Contain("Welcome(");
        }
    }

    [Theory(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    [InlineData(ApplyFromPreview)]
    [InlineData(ApplyFromListing)]
    [InlineData(ApplyWithoutProposal)]
    public async Task ResolvesAndAppliesRoslynCodeActionAsync(string mode)
    {
        const string title = "using System.Collections.Generic;";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = await workspace.AtAsync(
            LspAction.CodeActions,
            ProblemsTestFile,
            "List<string>"
        );
        var listing = await workspace.CallAsync(request);
        request = request with
        {
            ActionIndex = ActionIndex(
                listing,
                line => line.EndsWith(" " + title, StringComparison.Ordinal)
            ),
        };

        var preview = await workspace.CallAsync(request);
        (await DiagnosticsAsync(workspace)).Should().Contain(item => item.Code == "CS0246");

        var applyRequest = mode switch
        {
            ApplyFromListing => ApplyProposal(LspAction.CodeActions, listing) with
            {
                ActionIndex = request.ActionIndex,
            },
            ApplyFromPreview => ApplyProposal(LspAction.CodeActions, preview),
            _ => request with { Apply = true },
        };
        var applied = await workspace.CallAsync(applyRequest);
        applied.Should().BeApplied();

        Diagnostics(applied).Should().Contain(item => item.Code == "CS8603");
        Diagnostics(applied).Should().NotContain(item => item.Code == "CS0246");

        if (mode is not ApplyWithoutProposal)
        {
            (await workspace.CallRawAsync(applyRequest))
                .Should()
                .BeError(ErrorCodes.InvalidRequest);
        }

        var text = await workspace.ReadFileAsync(ProblemsTestFile);
        text.Should().Contain(title);

        var diagnostics = await DiagnosticsAsync(workspace);
        diagnostics.Should().NotContain(item => item.Code == "CS0246");
        diagnostics.Should().Contain(item => item.Code == "CS8603");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ListsSuppressionsOnlyOnRequestAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = new LspRequest
        {
            Action = LspAction.CodeActions,
            File = ProblemsTestFile,
            Diagnostic = "CS0246",
        };

        var hidden = Lines(await workspace.CallAsync(request));
        var all = Lines(await workspace.CallAsync(request with { All = true }));

        var suppressions = all.Where(line =>
                line.Contains(LspChanges.SuppressionGroup, StringComparison.Ordinal)
            )
            .ToArray();
        suppressions.Should().NotBeEmpty();
        hidden.Should().NotContain(suppressions);
        hidden[^1]
            .Split(' ')[0]
            .Should()
            .Be(suppressions.Length.ToString(CultureInfo.InvariantCulture));
        all.Should().Contain(hidden.Skip(1).SkipLast(1));
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task FixesEveryOccurrenceOfADiagnosticInADocumentAsync()
    {
        const string file = "Application/Usings.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await workspace.WriteFileAsync(
            file,
            "using System.IO;\nusing System.Text;\n\nnamespace Sample.Application;\n\npublic static class Usings;\n"
        );
        var request = new LspRequest
        {
            Action = LspAction.CodeActions,
            File = file,
            Diagnostic = "IDE0005",
        };

        var listing = await workspace.CallAsync(request);
        var applied = await workspace.CallAsync(
            ApplyProposal(LspAction.CodeActions, listing) with
            {
                ActionIndex = ActionIndex(
                    listing,
                    line =>
                        line.Contains("Fix All", StringComparison.Ordinal)
                        && line.EndsWith(" (Document)", StringComparison.Ordinal)
                ),
            }
        );

        applied.Should().BeApplied();
        (await workspace.ReadFileAsync(file)).Should().NotContain("using");
    }
}
