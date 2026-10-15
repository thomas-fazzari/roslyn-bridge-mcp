// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using RoslynBridge.Lsp;
using RoslynBridge.Requests;
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
    private const int RegexTimeoutMilliseconds = 1_000;
    private const string ApplyFromPreview = "preview";
    private const string ApplyFromListing = "listing";
    private const string ApplyWithoutProposal = "direct";
    private const string ContractFile = "Contracts/IGreeter.cs";
    private const string ImplementationFile = "Application/Greeter.cs";
    private const string ConsumerFile = "Application/Consumer.cs";
    private const string OtherGreeterFile = "Application/OtherGreeter.cs";
    private const string ProblemsFile = "Application/Problems.cs";

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
            ConsumerFile,
            "Greet("
        );
        var definition = await workspace.CallAsync(definitionRequest);
        AssertSingleLocation(definition, ContractFile);
        var target = await workspace.AtAsync(LspAction.Definition, ContractFile, "Greet(");
        definition.Should().Be($"{ContractFile} {target.Line}:{target.Character}");
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
                            ["uri"] = new Uri(workspace.FilePath(ConsumerFile)).AbsoluteUri,
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
            .Be(Path.GetFullPath(workspace.FilePath(ContractFile)));
        var rawStart = (rawLocation["range"] ?? rawLocation["targetSelectionRange"])!["start"]!;
        rawStart["line"]!.GetValue<int>().Should().Be(target.Line - 1);
        rawStart["character"]!.GetValue<int>().Should().Be(target.Character - 1);

        var typeDefinition = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.TypeDefinition, ConsumerFile, "greeter.Greet")
        );
        AssertSingleLocation(typeDefinition, ContractFile);

        var implementation = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Implementation, ContractFile, "Greet(")
        );
        AssertSingleLocation(implementation, ImplementationFile);

        var references = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.References, ContractFile, "Greet(")
        );
        Locations(references).Should().Contain(ConsumerFile);
        Locations(references).Should().NotContain(OtherGreeterFile);

        var hover = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Hover, ConsumerFile, "Greet(")
        );
        hover.Should().Contain("Greet").And.NotContain("```");

        var callers = Lines(
            await workspace.CallAsync(
                await workspace.AtAsync(LspAction.Callers, ContractFile, "Greet(")
            )
        );
        callers.Should().HaveCount(2);
        callers[0].Should().StartWith(ConsumerFile + ":");
        callers[1].Should().MatchRegex(@"^  calls \d+:\d+$");

        // A returned symbol name resolves back to its declaration
        AssertSingleLocation(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Definition, Symbol = SymbolName(callers[0]) }
            ),
            ConsumerFile
        );

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
        var consumer = Array.IndexOf(withContext, ConsumerFile);
        consumer.Should().BeGreaterThanOrEqualTo(0);
        withContext[consumer + 1].Should().Contain("greeter.Greet(");

        var subtypes = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Subtypes, ContractFile, "IGreeter")
        );
        Lines(subtypes).Select(FileOf).Should().Equal(ImplementationFile);

        var symbols = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, Query = "IGreeter" }
        );
        Lines(symbols).Should().Contain(line => line.Contains(" interface IGreeter"));

        var documentSymbols = Lines(
                await workspace.CallAsync(
                    new LspRequest { Action = LspAction.Symbols, File = ContractFile }
                )
            )
            .Select(SymbolName)
            .ToArray();
        documentSymbols.Should().Contain("IGreeter");
        documentSymbols
            .Should()
            .Contain(name => name.StartsWith("Greet(", StringComparison.Ordinal));

        var text = await workspace.ReadFileAsync(ProblemsFile);
        await workspace.WriteFileAsync(ProblemsFile, "using System.Collections.Generic;\n" + text);

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

        fullSummary.FilesChecked.Should().Be(4);
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
        var rename = await workspace.AtAsync(LspAction.Rename, ContractFile, "Greet(");
        await StartRoslynAsync(workspace);

        // The watcher reports the file, so synchronization tries to open it
        await workspace.WriteFileAsync(
            bigFile,
            "// " + new string('x', 1_100_000) + "\nnamespace Sample.Application;\n"
        );

        var preview = await workspace.CallAsync(rename with { NewName = "Welcome" });
        PreviewFiles(preview).Should().Contain(ConsumerFile);

        var diagnostics = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = ConsumerFile }
        );
        diagnostics.Should().NotContain("incomplete:");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RefreshesUnopenedFilesWithUnchangedMetadataBeforeRenameAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = await workspace.AtAsync(LspAction.References, ContractFile, "Greet(");
        var consumerPath = workspace.FilePath(ConsumerFile);

        var references = await workspace.CallAsync(request);
        Locations(references).Should().Contain(ConsumerFile);
        var rename = request with { Action = LspAction.Rename, NewName = "Welcome" };

        // first preview records fingerprints, second must detect an edit that keeps the metadata
        var initialPreview = await workspace.CallAsync(rename);
        PreviewFiles(initialPreview).Should().Contain(ConsumerFile);

        var original = await workspace.ReadFileAsync(ConsumerFile);
        var changed = original.Replace(
            "greeter.Greet(\"world\")",
            "OtherGreeter.Greet(\"\")",
            StringComparison.Ordinal
        );
        changed.Should().NotBe(original);
        var file = new FileInfo(consumerPath);
        var length = file.Length;
        var lastWriteTime = file.LastWriteTimeUtc;
        await workspace.WriteFileAsync(ConsumerFile, changed);
        File.SetLastWriteTimeUtc(consumerPath, lastWriteTime);
        file.Refresh();
        file.Length.Should().Be(length);
        file.LastWriteTimeUtc.Should().Be(lastWriteTime);

        var preview = await workspace.CallAsync(rename);
        PreviewFiles(preview).Should().BeEquivalentTo([ContractFile, ImplementationFile]);

        var refreshed = await workspace.CallAsync(request);
        Locations(refreshed).Should().NotContain(ConsumerFile);
        (await workspace.ReadFileAsync(ConsumerFile)).Should().Be(changed);
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
                File = ConsumerFile,
                Line = 7,
            }
        );
        Lines(enclosing)[0]
            .Should()
            .StartWith($"{ConsumerFile}:7-7 method Sample.Application.Consumer.Run(");
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

        reload.Should().Be("running: true");
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task ListsSameNamedTypesOfDifferentProjectsAsCandidatesAsync()
    {
        const string applicationProgram = "Application/Program.cs";
        const string contractsProgram = "Contracts/Program.cs";
        const string source =
            "namespace Sample.Application;\n\ninternal static class Program\n{\n    public static void Run() { }\n}\n";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(applicationProgram, source);
        await workspace.WriteFileAsync(contractsProgram, source);
        foreach (var file in new[] { applicationProgram, contractsProgram })
        {
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Diagnostics, File = file }
            );
        }

        var ambiguous = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Program.Run" }
        );
        AssertError(ambiguous, ErrorCodes.AmbiguousSymbol);
        Candidates(ambiguous)
            .Select(FileOf)
            .Should()
            .BeEquivalentTo([applicationProgram, contractsProgram]);

        AssertSingleLocation(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.Definition,
                    Symbol = "Program.Run",
                    File = contractsProgram,
                }
            ),
            contractsProgram
        );
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task NavigatesAndRenamesBySymbolNameAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var references = await workspace.CallAsync(
            new LspRequest { Action = LspAction.References, Symbol = "IGreeter.Greet" }
        );
        Locations(references).Should().Contain(ConsumerFile).And.NotContain(OtherGreeterFile);

        var ambiguous = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Greet" }
        );
        AssertError(ambiguous, ErrorCodes.AmbiguousSymbol);
        var candidates = Candidates(ambiguous).Select(SymbolName).ToArray();
        candidates.Should().HaveCount(3);
        var contract = candidates.Should().ContainSingle(name => name.Contains("IGreeter")).Which;
        AssertSingleLocation(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Definition, Symbol = contract }
            ),
            ContractFile
        );

        var preview = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                Symbol = "Consumer.Run",
                NewName = "Execute",
            }
        );
        PreviewFiles(preview).Should().Equal(ConsumerFile);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RenamesFilesOnlyWhenRoslynAdvertisesSupportAsync()
    {
        const string destination = "Application/RenamedGreeter.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, File = ImplementationFile }
        );
        var request = new LspRequest
        {
            Action = LspAction.RenameFile,
            File = ImplementationFile,
            NewName = destination,
        };

        var capabilities = JsonNode.Parse(
            await workspace.CallAsync(new LspRequest { Action = LspAction.Capabilities })
        );
        if (capabilities?["workspace"]?["fileOperations"]?["willRename"] is null)
        {
            var original = await workspace.ReadFileAsync(ImplementationFile);
            var rejected = await workspace.CallRawAsync(request);
            AssertError(rejected, ErrorCodes.InvalidRequest);
            (await workspace.ReadFileAsync(ImplementationFile)).Should().Be(original);
            File.Exists(workspace.FilePath(destination)).Should().BeFalse();
            TestContext.Current.TestOutputHelper!.WriteLine(
                "Roslyn did not advertise file rename support. Refusal preserved the source."
            );
            return;
        }

        var preview = await workspace.CallAsync(request);
        File.Exists(workspace.FilePath(ImplementationFile)).Should().BeTrue();
        File.Exists(workspace.FilePath(destination)).Should().BeFalse();

        var applied = await workspace.CallAsync(ApplyProposal(LspAction.RenameFile, preview));
        AssertApplied(applied);
        File.Exists(workspace.FilePath(ImplementationFile)).Should().BeFalse();
        File.Exists(workspace.FilePath(destination)).Should().BeTrue();

        var implementation = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Implementation, ContractFile, "Greet(")
        );
        AssertSingleLocation(implementation, destination);
        TestContext.Current.TestOutputHelper!.WriteLine(
            "Roslyn advertised file rename support. Preview, apply and navigation to the new path passed."
        );
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task AppliesRenameAcrossProjectsWithoutRenamingAHomonymAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var unrelatedBefore = await workspace.ReadFileAsync(OtherGreeterFile);
        var request = await workspace.AtAsync(LspAction.Rename, ContractFile, "Greet(");
        request = request with { NewName = "Welcome" };

        var preview = await workspace.CallAsync(request);
        var original = await workspace.ReadFileAsync(ContractFile);
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
        AssertApplied(applied);

        foreach (var file in new[] { ContractFile, ImplementationFile, ConsumerFile })
        {
            var content = await workspace.ReadFileAsync(file);
            content.Should().Contain("Welcome(");
            content.Should().NotContain("Greet(");
        }

        var unrelatedAfter = await workspace.ReadFileAsync(OtherGreeterFile);
        unrelatedAfter.Should().Be(unrelatedBefore);
    }

    [Fact(Explicit = true, Timeout = TestTimeoutMilliseconds)]
    public async Task RejectsRenamePreviewAfterAnExternalEditAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = await workspace.AtAsync(LspAction.Rename, ContractFile, "Greet(");
        request = request with { NewName = "Welcome" };

        var preview = await workspace.CallAsync(request);
        var original = await workspace.ReadFileAsync(ConsumerFile);
        var changed = original.Replace("world", "friend", StringComparison.Ordinal);
        await workspace.WriteFileAsync(ConsumerFile, changed);

        var rejected = await workspace.CallRawAsync(ApplyProposal(LspAction.Rename, preview));
        AssertError(rejected, ErrorCodes.StaleEdit);
        (await workspace.ReadFileAsync(ConsumerFile)).Should().Be(changed);
        var contract = await workspace.ReadFileAsync(ContractFile);
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

        AssertApplied(applied);
        foreach (var file in new[] { ContractFile, ImplementationFile, ConsumerFile })
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
        var request = await workspace.AtAsync(LspAction.CodeActions, ProblemsFile, "List<string>");
        var listing = await workspace.CallAsync(request);
        var action = Lines(listing)
            .Should()
            .ContainSingle(line => line.EndsWith(" " + title, StringComparison.Ordinal))
            .Which;
        request = request with
        {
            ActionIndex = int.Parse(
                action[..action.IndexOf(' ', StringComparison.Ordinal)],
                CultureInfo.InvariantCulture
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
        AssertApplied(applied);
        if (mode is not ApplyWithoutProposal)
        {
            AssertError(await workspace.CallRawAsync(applyRequest), ErrorCodes.InvalidRequest);
        }

        var text = await workspace.ReadFileAsync(ProblemsFile);
        text.Should().Contain(title);

        var diagnostics = await DiagnosticsAsync(workspace);
        diagnostics.Should().NotContain(item => item.Code == "CS0246");
        diagnostics.Should().Contain(item => item.Code == "CS8603");
    }

    private static LspRequest ApplyProposal(LspAction action, string preview) =>
        new()
        {
            Action = action,
            ProposalId = ProposalPattern().Match(preview).Groups["id"].Value,
            Apply = true,
        };

    private static void AssertApplied(string response) => response.Should().StartWith("applied:");

    private static void AssertError(CallToolResult response, string expectedCode)
    {
        response.IsError.Should().BeTrue();
        ErrorCode(response).Should().Be(expectedCode);
    }

    private static void AssertSingleLocation(string response, string expectedFile) =>
        Locations(response).Should().ContainSingle().Which.Should().Be(expectedFile, response);

    /// <summary>
    /// Starts Roslyn and waits for the project load, so a file written next reaches Roslyn
    /// as a change to the loaded workspace.
    /// </summary>
    private static async Task StartRoslynAsync(RoslynTestWorkspace workspace) =>
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = ConsumerFile }
        );

    private static async Task<Diagnostic[]> DiagnosticsAsync(RoslynTestWorkspace workspace) =>
        Diagnostics(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Diagnostics, File = ProblemsFile }
            )
        );

    private static string[] Lines(string text) => text.Split('\n');

    // Result lines that are not indented details, totals or notes start with a workspace file
    private static bool IsFileLine(string line) =>
        !line.StartsWith(' ')
        && !line.StartsWith("truncated:", StringComparison.Ordinal)
        && !line.StartsWith("incomplete:", StringComparison.Ordinal)
        && !SummaryPattern().IsMatch(line);

    private static string FileOf(string line)
    {
        var token = line.TrimStart().Split(' ')[0];
        var position = PositionSuffixPattern().Match(token);
        return position.Success ? token[..position.Index] : token;
    }

    // A symbol line ends with the name after its location and kind
    private static string SymbolName(string line)
    {
        var parts = line.TrimStart().Split(' ', 3);
        var name = parts[2];
        var container = name.IndexOf(" in ", StringComparison.Ordinal);
        return container < 0 ? name : name[..container];
    }

    private static string[] Locations(string response) =>
        [.. Lines(response).Where(IsFileLine).Select(FileOf)];

    private static string[] Candidates(CallToolResult response) =>
        [.. Lines(ResponseText(response)).Skip(1)];

    private static string[] PreviewFiles(string preview) =>
        [
            .. Lines(preview)
                .Select(line => OperationPattern().Match(line))
                .Where(match => match.Success)
                .Select(match => match.Groups["path"].Value),
        ];

    private static List<(string Before, string After)> Hunks(string preview)
    {
        var hunks = new List<(string Before, string After)>();
        foreach (var line in Lines(preview))
        {
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                hunks.Add((string.Empty, string.Empty));
            }
            else if (hunks.Count > 0 && line.StartsWith("- ", StringComparison.Ordinal))
            {
                hunks[^1] = (hunks[^1].Before + line[2..] + "\n", hunks[^1].After);
            }
            else if (hunks.Count > 0 && line.StartsWith("+ ", StringComparison.Ordinal))
            {
                hunks[^1] = (hunks[^1].Before, hunks[^1].After + line[2..] + "\n");
            }
        }

        return hunks;
    }

    private static Diagnostic[] Diagnostics(string response) =>
        [
            .. Lines(response)
                .Select(line => DiagnosticPattern().Match(line))
                .Where(match => match.Success)
                .Select(match => new Diagnostic(
                    match.Groups["severity"].Value,
                    match.Groups["code"].Value
                )),
        ];

    private static DiagnosticSummary Summary(string response)
    {
        var match = Lines(response)
            .Select(line => SummaryPattern().Match(line))
            .Single(m => m.Success);
        return new DiagnosticSummary(
            Number(match.Groups["checked"]),
            Number(match.Groups["files"]),
            Number(match.Groups["total"]),
            Number(match.Groups["below"])
        );

        static int Number(Group group) =>
            group.Success ? int.Parse(group.Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(
        @"^proposal (?<id>\w+)",
        RegexOptions.ExplicitCapture,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex ProposalPattern();

    [GeneratedRegex(
        @"^(?:change|create|delete) (?<path>.+)$|^rename .+ -> (?<path>.+)$",
        RegexOptions.None,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex OperationPattern();

    [GeneratedRegex(
        @"^  \d+:\d+ (?<severity>\w+) (?<code>\S+) ",
        RegexOptions.ExplicitCapture,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex DiagnosticPattern();

    [GeneratedRegex(
        @"^(?<checked>\d+) files? checked, (?:no diagnostics|(?<total>\d+) diagnostics? in (?<files>\d+) files?)(?:, (?<below>\d+) below the severity)?$",
        RegexOptions.None,
        RegexTimeoutMilliseconds
    )]
    private static partial Regex SummaryPattern();

    [GeneratedRegex(@":\d+:\d+$", RegexOptions.ExplicitCapture, RegexTimeoutMilliseconds)]
    private static partial Regex PositionSuffixPattern();

    private readonly record struct Diagnostic(string Severity, string Code);

    private readonly record struct DiagnosticSummary(
        int FilesChecked,
        int FilesWithDiagnostics,
        int Total,
        int BelowSeverity
    );
}
