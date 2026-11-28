// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Features.Edit.CodeActions;
using RoslynBridge.Lsp;
using RoslynBridge.Lsp.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using static RoslynBridge.Tests.Assertions.ResultLines;

namespace RoslynBridge.Tests.Integration.Roslyn;

public sealed partial class RoslynMcpTests
{
    private const int TEST_TIMEOUT_MILLISECONDS = 300_000;

    private const string APPLY_FROM_PREVIEW = "preview";
    private const string APPLY_FROM_LISTING = "listing";
    private const string APPLY_WITHOUT_PROPOSAL = "direct";
    private const string LOADING_LINE = "loading: true";
    private const string IDLE_LINE = "loading: false";
    private const string PROGRESS_LINE = "progress:";

    private const string CONTRACT_TEST_PROJECT = "Contracts/Contracts.csproj";
    private const string CONTRACT_TEST_FILE = "Contracts/IGreeter.cs";
    private const string IMPLEMENTATION_TEST_FILE = "Application/Greeter.cs";
    private const string CONSUMER_TEST_FILE = "Application/Consumer.cs";
    private const string OTHER_GREETER_TEST_FILE = "Application/OtherGreeter.cs";
    private const string PROBLEMS_TEST_FILE = "Application/Problems.cs";
    private const string APPLICATION_PROGRAM_TEST_FILE = "Application/Program.cs";
    private const string CONTRACTS_PROGRAM_TEST_FILE = "Contracts/Program.cs";
    private const string PROGRAM_FILE_NAME = "Program.cs";
    private const string TOTAL_TEST_SYMBOL = "Counter.Total";
    private const string STEP_TEST_SYMBOL = "Counter.Step";
    private const string UNUSED_FIXTURE = "Unused";
    private const string UNUSED_KEPT_FIXTURE = "UnusedKept";
    private const string MULTI_TARGET_FIXTURE = "MultiTarget";
    private const string BROKEN_PROJECT_FIXTURE = "BrokenProject";
    private const string BROKEN_TEST_PROJECT = "Broken/Broken.csproj";
    private const string LEFTOVERS_TEST_FILE = "Application/Leftovers.cs";
    private const string LEFTOVER_CHECKS_TEST_FILE = "Checks/LeftoverChecks.cs";
    private const string KEPT_TEST_FILE = "Application/Kept.cs";
    private const string EXPRESSIONS_TEST_FILE = "Application/Expressions.cs";
    private const string EXPRESSIONS_SOURCE = """
        namespace Sample.Application;

        public static class Formatter
        {
            private const int Step = 40;

            /// <summary>Formats an <see cref="int"/> value.</summary>
            public static string Format(int value) => value.ToString();

            public static string Format(string value) => value;

            public static string Describe(string? text)
            {
                var length = Format(42).Length;
                long wide = Step;
                if (text is null)
                    return string.Empty;

                return Format(text) + length + wide;
            }

            public static string Broken() => Format(1.5);

            public static T Echo<T>(T value) => value;

            public static int Echoed() => Echo(7);
        }
        """;
    private const string IMPLICIT_CALLS_TEST_FILE = "Application/ImplicitCalls.cs";
    private const string IMPLICIT_CALLS_SOURCE = """
        using System;
        using System.Runtime.CompilerServices;
        using System.Threading.Tasks;

        namespace Sample.Application;

        public sealed class Numbers
        {
            public Enumerator GetEnumerator() => new();

            public sealed class Enumerator : IDisposable
            {
                public int Current => 0;

                public bool MoveNext() => false;

                public void Dispose() { }
            }
        }

        public readonly struct Later
        {
            public Awaiter GetAwaiter() => new();

            public readonly struct Awaiter : INotifyCompletion
            {
                public bool IsCompleted => true;

                public int GetResult() => 1;

                public void OnCompleted(Action continuation) { }
            }
        }

        public sealed class Pair
        {
            public void Deconstruct(out int left, out int right)
            {
                left = 0;
                right = 0;
            }
        }

        public sealed class Items
        {
            public Items Select(Func<int, int> selector) => this;

            public Items Where(Func<int, bool> predicate) => this;
        }

        public sealed class Resource : IDisposable
        {
            public void Dispose() { }
        }

        public readonly struct Amount
        {
            public static Amount operator +(Amount left, Amount right) => left;
        }

        public static class Uses
        {
            public static void Loop(Numbers numbers)
            {
                foreach (var number in numbers) { }
            }

            public static async Task WaitAsync(Later later)
            {
                await later;
            }

            public static bool Match(object value) => value is Pair(var left, _);

            public static Items Query(Items items) =>
                from item in items where item > 1 select item + 1;

            public static async Task<Amount> EverythingAsync(
                Numbers numbers,
                Later later,
                Amount amount
            )
            {
                foreach (var number in numbers) { }

                await later;
                using var resource = new Resource();
                return amount + Identity(amount);
            }

            private static Amount Identity(Amount amount) => amount;
        }
        """;
    private const string JSON_CONTEXT_TEST_FILE = "Application/JsonContext.cs";
    private const string JSON_CONTEXT_SOURCE = """
        using System.Text.Json;
        using System.Text.Json.Serialization;

        namespace Sample.Application;

        [JsonSerializable(typeof(int))]
        internal partial class Ctx : JsonSerializerContext { }

        public static class JsonUse
        {
            internal static object Use() => Ctx.Default;

            internal static object Create() => new Ctx(new JsonSerializerOptions());
        }

        public sealed class ObjectUse
        {
            public ObjectUse()
            {
                _ = new object();
            }
        }
        """;

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
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
            CONSUMER_TEST_FILE,
            "Greet("
        );
        var definition = await workspace.CallAsync(definitionRequest);
        definition.Should().HaveSingleLocation(CONTRACT_TEST_FILE);
        var target = await workspace.AtAsync(LspAction.Definition, CONTRACT_TEST_FILE, "Greet(");
        definition
            .Should()
            .Be(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{CONTRACT_TEST_FILE} {target.Line}:{target.Character}"
                )
            );
        var raw = JsonNode.Parse(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.Request,
                    Method = LspMethods.TEXT_DOCUMENT_DEFINITION,
                    Parameters = new JsonObject
                    {
                        ["textDocument"] = new JsonObject
                        {
                            ["uri"] = new Uri(workspace.FilePath(CONSUMER_TEST_FILE)).AbsoluteUri,
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
            .TryGetWorkspaceFile(rawUri)!
            .FullPath.Should()
            .Be(Path.GetFullPath(workspace.FilePath(CONTRACT_TEST_FILE)));
        var rawStart = (rawLocation["range"] ?? rawLocation["targetSelectionRange"])!["start"]!;
        rawStart["line"]!.GetValue<int>().Should().Be(target.Line - 1);
        rawStart["character"]!.GetValue<int>().Should().Be(target.Character - 1);

        var typeDefinition = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.TypeDefinition, CONSUMER_TEST_FILE, "greeter.Greet")
        );
        typeDefinition.Should().HaveSingleLocation(CONTRACT_TEST_FILE);

        var implementation = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Implementation, CONTRACT_TEST_FILE, "Greet(")
        );
        implementation.Should().HaveSingleLocation(IMPLEMENTATION_TEST_FILE);

        var references = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.References, CONTRACT_TEST_FILE, "Greet(")
        );
        Locations(references).Should().Contain(CONSUMER_TEST_FILE);
        Locations(references).Should().NotContain(OTHER_GREETER_TEST_FILE);

        var hover = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Hover, CONSUMER_TEST_FILE, "Greet(")
        );
        hover.Should().Contain("Greet").And.NotContain("```");

        var callers = Lines(
            await workspace.CallAsync(
                await workspace.AtAsync(LspAction.Callers, CONTRACT_TEST_FILE, "Greet(")
            )
        );
        callers.Should().HaveCount(2);
        callers[0].Should().StartWith(CONSUMER_TEST_FILE + ":");
        callers[1].Should().MatchRegex(@"^  calls \d+:\d+$");

        // A returned symbol name resolves back to its declaration
        var resolved = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = SymbolName(callers[0]) }
        );
        resolved.Should().HaveSingleLocation(CONSUMER_TEST_FILE);

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
        var consumer = Array.IndexOf(withContext, CONSUMER_TEST_FILE);
        consumer.Should().BeGreaterThanOrEqualTo(0);
        withContext[consumer + 1].Should().Contain("greeter.Greet(");

        var subtypes = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Subtypes, CONTRACT_TEST_FILE, "IGreeter")
        );
        Lines(subtypes).Select(FileOf).Should().Equal(IMPLEMENTATION_TEST_FILE);

        var symbols = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, Query = "IGreeter" }
        );
        Lines(symbols).Should().Contain(line => line.Contains(" interface IGreeter"));

        var documentSymbols = Lines(
                await workspace.CallAsync(
                    new LspRequest { Action = LspAction.Symbols, File = CONTRACT_TEST_FILE }
                )
            )
            .Select(SymbolName)
            .ToArray();
        documentSymbols.Should().Contain("IGreeter");
        documentSymbols
            .Should()
            .Contain(name => name.StartsWith("Greet(", StringComparison.Ordinal));

        var text = await workspace.ReadFileAsync(PROBLEMS_TEST_FILE);
        await workspace.WriteFileAsync(
            PROBLEMS_TEST_FILE,
            "using System.Collections.Generic;\n" + text
        );

        var refreshed = await DiagnosticsAsync(workspace);
        refreshed.Should().NotContain(item => item.Code == "CS0246");
        refreshed.Should().Contain(item => item.Code == "CS8603");

        (await workspace.StopAsync()).Should().Be(0);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task DiagnosticsOmitCleanFilesAndKeepCountsWhenTruncatedAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        const string CLEAN_FILE = "Contracts/Clean.cs";
        await workspace.WriteFileAsync(CLEAN_FILE, string.Empty);
        var clean = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = CLEAN_FILE }
        );
        clean.Should().Be("1 file checked, no diagnostics");

        var request = new LspRequest
        {
            Action = LspAction.Diagnostics,
            File = "Application/*.cs",
            Limit = Paging.MAXIMUM_LIMIT,
        };
        var full = await workspace.CallAsync(request);
        var limited = await workspace.CallAsync(request with { Limit = 1 });
        var fullSummary = Summary(full);

        fullSummary.FilesChecked.Should().Be(8);
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

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ImmediatelyReportsSemanticDiagnosticsForANewFileAsync()
    {
        const string NEW_FILE = "Application/NewFile.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);

        // Roslyn first treats a new file as a miscellaneous file, without semantic diagnostics
        await workspace.WriteFileAsync(
            NEW_FILE,
            "namespace Sample.Application;\n\npublic static class NewFile\n{\n    public static int Wrong() => \"text\";\n}\n"
        );
        var result = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = NEW_FILE }
        );

        result.Should().NotContain("incomplete:");
        Diagnostics(result).Should().Contain(item => item.Code == "CS0029");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task WaitsOnlyOnceForANewFileOutsideEveryProjectAsync()
    {
        const string LOOSE_FILE = "Loose.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(LOOSE_FILE, "internal static class Loose\n{\n}\n");
        var request = new LspRequest { Action = LspAction.Diagnostics, File = LOOSE_FILE };
        await workspace.CallAsync(request);

        var started = Stopwatch.GetTimestamp();
        var again = await workspace.CallAsync(request);

        Stopwatch
            .GetElapsedTime(started)
            .Should()
            .BeLessThan(RoslynConnection.ProjectAttachTimeout);
        again.Should().Contain("incomplete:");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task SkipsOversizedDocumentsDuringSynchronizationAsync()
    {
        const string BIG_FILE = "Application/Big.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var rename = await workspace.AtAsync(LspAction.Rename, CONTRACT_TEST_FILE, "Greet(");
        await StartRoslynAsync(workspace);

        // The watcher reports the file, so synchronization tries to open it
        await workspace.WriteFileAsync(
            BIG_FILE,
            "// " + new string('x', 1_100_000) + "\nnamespace Sample.Application;\n"
        );

        var preview = await workspace.CallAsync(rename with { NewName = "Welcome" });
        PreviewFiles(preview).Should().Contain(CONSUMER_TEST_FILE);

        var diagnostics = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = CONSUMER_TEST_FILE }
        );
        diagnostics.Should().NotContain("incomplete:");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ResolvesSymbolsInFilesDeclaringComparisonOperatorsAsync()
    {
        const string MONEY_FILE = "Application/Money.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            MONEY_FILE,
            "namespace Sample.Application;\n\npublic readonly record struct Money(decimal Amount)\n{\n"
                + "    public Money Add(Money other) => new(Amount + other.Amount);\n"
                + "    public static bool operator <(Money left, Money right) => left.Amount < right.Amount;\n"
                + "    public static bool operator >(Money left, Money right) => left.Amount > right.Amount;\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = MONEY_FILE }
        );

        var definition = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Money.Add" }
        );

        Locations(definition).Should().Equal(MONEY_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsReferencesOfAnOverrideWithoutItsSiblingsAsync()
    {
        const string CIRCLE_FILE = "Application/Circle.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            CIRCLE_FILE,
            "namespace Sample.Application;\n\npublic sealed class Circle() : Shape(\"circle\")\n{\n"
                + "    public override double Area() => 3;\n}\n\npublic static class Areas\n{\n"
                + "    public static double OfSquare(Square square) => square.Area();\n\n"
                + "    public static double OfCircle(Circle circle) => circle.Area();\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = CIRCLE_FILE }
        );

        var references = Lines(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.References,
                    Symbol = "Square.Area",
                    Context = true,
                }
            )
        );

        // A search that cascades through Shape.Area would also list the sibling override's call
        references
            .Should()
            .Contain(line => line.Contains("square.Area()", StringComparison.Ordinal));
        references
            .Should()
            .NotContain(line => line.Contains("circle.Area()", StringComparison.Ordinal));
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsCallersOfAnImplementationThroughItsInterfaceAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var callers = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Greeter.Greet" }
        );

        // Consumer only calls IGreeter.Greet, which Greeter implements
        callers
            .Should()
            .Be(
                $"""
                {CONSUMER_TEST_FILE}:7:26 method Sample.Application.Consumer.Run(IGreeter)
                  through IGreeter.Greet(string)
                  calls 7:59
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task KeepsOnlyTheCallsThroughABaseMemberWhoseReceiverCanHoldTheTypeAsync()
    {
        const string LEASES_FILE = "Application/Leases.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            LEASES_FILE,
            "namespace Sample.Application;\n\npublic interface IHandle : System.IDisposable { }\n\n"
                + "public sealed class Lease : System.IDisposable\n{\n    public void Dispose() { }\n}\n\n"
                + "public static class Leases\n{\n    public static IHandle Open() => null!;\n\n"
                + "    public static void Release(System.IDisposable lease) => lease.Dispose();\n\n"
                + "    public static void Hold(IHandle handle) => handle.Dispose();\n\n"
                + "    public static void Keep()\n    {\n        var handle = Open();\n    }\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = LEASES_FILE }
        );

        var callers = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Lease.Dispose" }
        );

        // A sealed Lease is never an IHandle, so Hold and the declaration in Keep cannot reach it
        callers
            .Should()
            .Be(
                $"""
                {LEASES_FILE}:14:24 method Sample.Application.Leases.Release(IDisposable)
                  through IDisposable.Dispose()
                  calls 14:67
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ReportsTheCompilerFactsOfExpressionsAndDeclaredNamesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await WriteExpressionsAsync(workspace);

        var overload = await ExpressionAtAsync(workspace, "Format(42)");
        var constant = await ExpressionAtAsync(workspace, "Step;");
        var beforePunctuation = await ExpressionAtAsync(workspace, "Step;", "Step".Length);
        var instantiated = await ExpressionAtAsync(workspace, "Echo(7)");
        var beforeCheck = await ExpressionAtAsync(workspace, "text is");
        var afterCheck = await ExpressionAtAsync(workspace, "text) +");
        var inferred = await ExpressionAtAsync(workspace, "length =");
        var metadata = await ExpressionAtAsync(workspace, "ToString()");
        var declared = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Expression, Symbol = "Formatter.Step" }
        );

        overload
            .Should()
            .Be(
                $"""
                expression Format(42)
                kind InvocationExpression
                type string
                nullability NotAnnotated/NotNull
                {EXPRESSIONS_TEST_FILE}:8:26 method Sample.Application.Formatter.Format(int)
                  doc Formats an Int32 value.
                """
            );
        constant
            .Should()
            .Be(
                $"""
                expression Step
                kind IdentifierName
                type int
                converted long
                nullability NotAnnotated/NotNull
                conversion implicit numeric
                constant 40
                {EXPRESSIONS_TEST_FILE}:5:23 constant Sample.Application.Formatter.Step
                """
            );
        beforePunctuation.Should().Be(constant);
        // The null check changes the flow state of the same parameter
        beforeCheck
            .Should()
            .Be(
                $"""
                expression text
                kind IdentifierName
                type string?
                nullability Annotated/MaybeNull
                {EXPRESSIONS_TEST_FILE}:12:43 variable text
                """
            );
        afterCheck
            .Should()
            .Be(
                $"""
                expression text
                kind IdentifierName
                type string
                nullability NotAnnotated/NotNull
                {EXPRESSIONS_TEST_FILE}:12:43 variable text
                """
            );
        inferred
            .Should()
            .Be(
                $"""
                declaration length
                kind VariableDeclarator
                type int
                nullability NotAnnotated/NotNull
                {EXPRESSIONS_TEST_FILE}:14:13 variable length
                """
            );
        declared
            .Should()
            .Be(
                $"""
                declaration Step
                kind VariableDeclarator
                type int
                nullability NotAnnotated/NotNull
                constant 40
                {EXPRESSIONS_TEST_FILE}:5:23 constant Sample.Application.Formatter.Step
                """
            );
        instantiated
            .Should()
            .Be(
                $"""
                expression Echo(7)
                kind InvocationExpression
                type int
                nullability NotAnnotated/NotNull
                {EXPRESSIONS_TEST_FILE}:24:21 method Sample.Application.Formatter.Echo<T>(T)
                  instantiation Sample.Application.Formatter.Echo<int>(int)
                """
            );
        // A symbol from metadata has no declaration to show
        Lines(metadata).Should().Contain("method int.ToString()");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task GivesASourceGeneratedSymbolNoPositionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(JSON_CONTEXT_TEST_FILE, JSON_CONTEXT_SOURCE);
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = JSON_CONTEXT_TEST_FILE }
        );

        var generated = await ExpressionAtAsync(
            workspace,
            "Default;",
            file: JSON_CONTEXT_TEST_FILE
        );

        // The generated declaration is in a path relative to its generator, so it has no position
        Lines(generated)
            .Should()
            .Contain("property Ctx.Default")
            .And.NotContain(line => line.Contains(".cs:", StringComparison.Ordinal));
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsASourceGeneratedCalleeWithoutAPositionAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(JSON_CONTEXT_TEST_FILE, JSON_CONTEXT_SOURCE);
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = JSON_CONTEXT_TEST_FILE }
        );

        var callees = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callees, Symbol = "JsonUse.Create" }
        );

        callees
            .Should()
            .Be(
                """
                constructor Ctx.Ctx(JsonSerializerOptions)
                  calls 13:40
                1 callees outside the workspace omitted
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task DoesNotCountTheImplicitObjectConstructorCallAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(JSON_CONTEXT_TEST_FILE, JSON_CONTEXT_SOURCE);
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = JSON_CONTEXT_TEST_FILE }
        );

        var callees = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callees, Symbol = "ObjectUse.ObjectUse()" }
        );

        // The explicit new object() counts, the implicit base() of the constructor does not
        callees.Should().Be("no callees\n1 callees outside the workspace omitted");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsTheCandidatesOfAnUnresolvableOverloadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await WriteExpressionsAsync(workspace);

        var unbound = await ExpressionAtAsync(workspace, "Format(1.5)");

        unbound
            .Should()
            .Be(
                $"""
                expression Format(1.5)
                kind InvocationExpression
                type string
                nullability NotAnnotated/NotNull
                no symbol: OverloadResolutionFailure
                  {EXPRESSIONS_TEST_FILE}:8:26 method Sample.Application.Formatter.Format(int)
                  {EXPRESSIONS_TEST_FILE}:10:26 method Sample.Application.Formatter.Format(string)
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsEachCallerOnceInAMultiTargetedProjectAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            overlay: MULTI_TARGET_FIXTURE
        );

        var callers = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Greeter.Greet" }
        );

        callers.Should().HaveSingleLocation(CONSUMER_TEST_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsTheCompilerInsertedCallersOfAnEnumeratorAndAnAwaiterAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await WriteImplicitCallsAsync(workspace);

        var dispose = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Numbers.Enumerator.Dispose" }
        );
        var result = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Later.Awaiter.GetResult" }
        );

        // The call site is the foreach or await keyword, as for a using
        dispose
            .Should()
            .Be(
                $"""
                {IMPLICIT_CALLS_TEST_FILE}:63:24 method Sample.Application.Uses.Loop(Numbers)
                  calls 65:9
                {IMPLICIT_CALLS_TEST_FILE}:78:38 method Sample.Application.Uses.EverythingAsync(Numbers, Later, Amount)
                  calls 84:9
                """
            );
        result
            .Should()
            .Be(
                $"""
                {IMPLICIT_CALLS_TEST_FILE}:68:30 method Sample.Application.Uses.WaitAsync(Later)
                  calls 70:9
                {IMPLICIT_CALLS_TEST_FILE}:78:38 method Sample.Application.Uses.EverythingAsync(Numbers, Later, Amount)
                  calls 86:9
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsTheCompilerInsertedCallersOfAPatternAndAQueryAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await WriteImplicitCallsAsync(workspace);

        var deconstruct = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Pair.Deconstruct" }
        );
        var where = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Items.Where" }
        );
        var select = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "Items.Select" }
        );

        // The call site is the type of the pattern and the keyword of the query clause
        deconstruct
            .Should()
            .Be(
                $"""
                {IMPLICIT_CALLS_TEST_FILE}:73:24 method Sample.Application.Uses.Match(object)
                  calls 73:56
                """
            );
        where
            .Should()
            .Be(
                $"""
                {IMPLICIT_CALLS_TEST_FILE}:75:25 method Sample.Application.Uses.Query(Items)
                  calls 76:28
                """
            );
        select
            .Should()
            .Be(
                $"""
                {IMPLICIT_CALLS_TEST_FILE}:75:25 method Sample.Application.Uses.Query(Items)
                  calls 76:43
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsTheCompilerInsertedCalleesOfAMethodAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await WriteImplicitCallsAsync(workspace);

        var callees = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callees, Symbol = "Uses.EverythingAsync" }
        );

        // Only members declared in the workspace are listed. Current and IsCompleted are accesses
        callees
            .Should()
            .Be(
                $"""
                {IMPLICIT_CALLS_TEST_FILE}:9:23 method Sample.Application.Numbers.GetEnumerator()
                  calls 84:9
                {IMPLICIT_CALLS_TEST_FILE}:15:21 method Sample.Application.Numbers.Enumerator.MoveNext()
                  calls 84:9
                {IMPLICIT_CALLS_TEST_FILE}:17:21 method Sample.Application.Numbers.Enumerator.Dispose()
                  calls 84:9
                {IMPLICIT_CALLS_TEST_FILE}:23:20 method Sample.Application.Later.GetAwaiter()
                  calls 86:9
                {IMPLICIT_CALLS_TEST_FILE}:29:20 method Sample.Application.Later.Awaiter.GetResult()
                  calls 86:9
                {IMPLICIT_CALLS_TEST_FILE}:51:21 constructor Sample.Application.Resource
                  calls 87:30
                {IMPLICIT_CALLS_TEST_FILE}:53:17 method Sample.Application.Resource.Dispose()
                  calls 87:9
                {IMPLICIT_CALLS_TEST_FILE}:58:35 operator Sample.Application.Amount.operator +(Amount, Amount)
                  calls 88:23
                {IMPLICIT_CALLS_TEST_FILE}:91:27 method Sample.Application.Uses.Identity(Amount)
                  calls 88:25
                2 property, field or constant accesses omitted
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task CountsGeneratedPartsOfImplementationsWithoutListingThemAsync()
    {
        const string MATCHER_FILE = "Application/Matcher.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            MATCHER_FILE,
            "using System.Text.RegularExpressions;\nusing Sample.Contracts;\n\n"
                + "namespace Sample.Application;\n\n"
                + "public sealed partial class Matcher : IGreeter\n{\n"
                + "    public string Greet(string name) => Pattern().Replace(name, \"\");\n\n"
                + "    [GeneratedRegex(\"a\")]\n    private static partial Regex Pattern();\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = MATCHER_FILE }
        );

        var implementations = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Implementation, Symbol = "IGreeter" }
        );

        // The regex generator adds a second partial declaration of Matcher
        implementations
            .Should()
            .Be(
                $"""
                {IMPLEMENTATION_TEST_FILE} 5:21
                {MATCHER_FILE} 6:29
                generated code: 1 more omitted. Use lsp request to list them.
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsReadsThroughAnInterfaceAndTheBaseInOneFileAsync()
    {
        const string BOX_FILE = "Application/Box.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            BOX_FILE,
            "namespace Sample.Application;\n\npublic interface ISized\n{\n    int Size { get; }\n}\n\n"
                + "public class Box\n{\n    public int Size { get; set; }\n}\n\n"
                + "public sealed class Crate : Box, ISized { }\n\n"
                + "public static class Sizes\n{\n"
                + "    public static int OfSized(ISized sized) => sized.Size;\n\n"
                + "    public static int OfBox(Box box) => box.Size;\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = BOX_FILE }
        );

        var references = await workspace.CallAsync(
            new LspRequest { Action = LspAction.References, Symbol = "Box.Size" }
        );
        var reads = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Reads, Symbol = "Box.Size" }
        );

        // Box.Size implements ISized.Size for Crate. A highlight from the file's first reference,
        // which goes through ISized, would miss box.Size
        references.Should().Be($"{BOX_FILE} 17:54 19:45");
        reads.Should().Be($"{BOX_FILE} 17:54 19:45");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ReadsSourceAndMembersAcrossPartialDeclarationsAsync()
    {
        const string LEDGER_FILE = "Application/Ledger.cs";
        const string TOTALS_FILE = "Application/Ledger.Totals.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            LEDGER_FILE,
            "namespace Sample.Application;\n\npublic partial class Ledger\n{\n"
                + "    /// <summary>Adds an amount.</summary>\n"
                + "    public void Add(decimal amount) => Total += amount;\n}\n"
        );
        await workspace.WriteFileAsync(
            TOTALS_FILE,
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
        members.Should().Contain([LEDGER_FILE, TOTALS_FILE]);
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
            .Be($"{LEDGER_FILE}:5-6 method Sample.Application.Ledger.Add(decimal)");
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
            .BeEquivalentTo([LEDGER_FILE, TOTALS_FILE]);

        var enclosing = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Source,
                File = CONSUMER_TEST_FILE,
                Line = 7,
            }
        );
        Lines(enclosing)[0]
            .Should()
            .StartWith($"{CONSUMER_TEST_FILE}:7-7 method Sample.Application.Consumer.Run(");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsWhatBaseClassesPassOnAfterTheOwnMembersAsync()
    {
        const string SHAPES_FILE = "Application/Shapes.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var request = new LspRequest
        {
            Action = LspAction.Members,
            Symbol = "Square",
            Inherited = true,
        };

        var members = await workspace.CallAsync(request);
        var page = await workspace.CallAsync(request with { Offset = 3, Limit = 2 });

        members
            .Should()
            .Be(
                $"""
                class Sample.Application.Square
                {SHAPES_FILE}
                  30:12 constructor Square(double)
                  33:19 property Side : double
                  35:28 method Area() : double
                inherited from Sample.Application.Polygon
                {SHAPES_FILE}
                  23:16 property Sides : int
                inherited from Sample.Application.Shape
                {SHAPES_FILE}
                  11:19 property Name : string
                  15:20 method Touch() : void
                """
            );
        page.Should()
            .Be(
                $"""
                class Sample.Application.Square
                inherited from Sample.Application.Polygon
                {SHAPES_FILE}
                  23:16 property Sides : int
                inherited from Sample.Application.Shape
                {SHAPES_FILE}
                  11:19 property Name : string
                truncated: 6 members in total, next offset: 5. Raise limit or narrow the query.
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task GathersTheImpactOfAMemberInOneCallAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var impact = Lines(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Impact, Symbol = "IGreeter.Greet" }
            )
        );

        Section(impact, "references").Select(FileOf).Should().Contain(CONSUMER_TEST_FILE);
        Section(impact, "callers")
            .Should()
            .ContainSingle(line =>
                line.StartsWith(CONSUMER_TEST_FILE + ":", StringComparison.Ordinal)
            );
        Section(impact, "implementation").Select(FileOf).Should().Contain(IMPLEMENTATION_TEST_FILE);
        impact
            .Should()
            .NotContain(line => line.StartsWith("## subtypes", StringComparison.Ordinal));

        var typeImpact = Lines(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Impact, Symbol = "IGreeter" }
            )
        );
        Section(typeImpact, "subtypes").Select(FileOf).Should().Equal(IMPLEMENTATION_TEST_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task NavigatesAHierarchyFromAUsageLikeFromItsDeclarationAsync()
    {
        // greeter.Greet("world") in Consumer.cs, and Bump(ref Total) in Counter.cs
        const int GREET_LINE = 7;
        const int GREET_CHARACTER = 59;
        const string COUNTER_FILE = "Application/Counter.cs";
        const int BUMP_LINE = 14;
        const int BUMP_CHARACTER = 9;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var declaredCallers = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Callers, Symbol = "IGreeter.Greet" }
        );
        var declaredImpact = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Impact, Symbol = "Counter.Bump" }
        );

        var callers = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Callers,
                File = CONSUMER_TEST_FILE,
                Line = GREET_LINE,
                Character = GREET_CHARACTER,
            }
        );
        var impact = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Impact,
                File = COUNTER_FILE,
                Line = BUMP_LINE,
                Character = BUMP_CHARACTER,
            }
        );

        callers.Should().Be(declaredCallers);
        impact.Should().Be(declaredImpact);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsTheReadsAndWritesOfAFieldAndAPropertyAcrossFilesAsync()
    {
        const string COUNTER_FILE = "Application/Counter.cs";
        const string READER_FILE = "Application/CounterReader.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var totalReads = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Reads, Symbol = TOTAL_TEST_SYMBOL }
        );
        var totalWrites = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Writes, Symbol = TOTAL_TEST_SYMBOL }
        );
        var stepReads = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Reads, Symbol = STEP_TEST_SYMBOL }
        );
        var stepWrites = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Writes, Symbol = STEP_TEST_SYMBOL }
        );

        totalWrites
            .Should()
            .Be($"{COUNTER_FILE} 11:9 12:9 13:9 14:18 15:18 19:51\n{READER_FILE} 7:58");
        totalReads.Should().Be($"{COUNTER_FILE} 14:18 16:24 18:55 19:51\n{READER_FILE} 5:56");
        stepReads.Should().Be($"{COUNTER_FILE} 12:18");
        stepWrites.Should().Be($"{COUNTER_FILE} 17:9");

        var status = await workspace.CallAsync(new LspRequest { Action = LspAction.Status });
        status.Should().Contain(IDLE_LINE).And.NotContain(PROGRESS_LINE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task RunsSeveralNavigationsOfOneSymbolInOneBulkCallAsync()
    {
        const string SYMBOL = "IGreeter.Greet";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var bulk = Lines(
            await workspace.CallAsync(
                new LspRequest
                {
                    Action = LspAction.Bulk,
                    Queries =
                    [
                        new LspRequest { Action = LspAction.Source, Symbol = SYMBOL },
                        new LspRequest { Action = LspAction.References, Symbol = SYMBOL },
                        new LspRequest { Action = LspAction.Callers, Symbol = SYMBOL },
                        new LspRequest { Action = LspAction.Impact, Symbol = SYMBOL },
                    ],
                }
            )
        );

        bulk.Where(line => line.StartsWith("## ", StringComparison.Ordinal))
            .Should()
            .Equal(
                $"## 1 source {SYMBOL}",
                $"## 2 references {SYMBOL}",
                $"## 3 callers {SYMBOL}",
                $"## 4 impact {SYMBOL}"
            );
        bulk.Should().NotContain(line => line.StartsWith("error ", StringComparison.Ordinal));
        Section(bulk, $"1 source {SYMBOL}").Should().Contain(line => line.Contains("string Greet"));
        Section(bulk, $"2 references {SYMBOL}").Select(FileOf).Should().Contain(CONSUMER_TEST_FILE);
        Section(bulk, $"3 callers {SYMBOL}")
            .Should()
            .ContainSingle(line =>
                line.StartsWith(CONSUMER_TEST_FILE + ":", StringComparison.Ordinal)
            );
        bulk.Should().Contain("### references").And.Contain("### callers");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
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
            status.Contains(LOADING_LINE, StringComparison.Ordinal)
        );
        loading.Should().NotBeEmpty("a status call during the reload sees the load");
        loading
            .Should()
            .Contain(status => status.Contains(PROGRESS_LINE, StringComparison.Ordinal));
        var after = await workspace.CallAsync(new LspRequest { Action = LspAction.Status });
        after.Should().Contain(IDLE_LINE).And.NotContain(PROGRESS_LINE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task LoadsOnlyTheProjectsOfTheSettingsFileAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            settings: $$"""{ "projects": ["{{CONTRACT_TEST_PROJECT}}"] }"""
        );

        var symbols = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, Query = "Greeter" }
        );

        symbols.Should().Contain(CONTRACT_TEST_FILE).And.NotContain(IMPLEMENTATION_TEST_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task StatusListsAProjectThatFailedToLoadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            settings: $$"""{ "projects": ["{{CONTRACT_TEST_PROJECT}}", "{{BROKEN_TEST_PROJECT}}"] }""",
            overlay: BROKEN_PROJECT_FIXTURE
        );
        await workspace.CallAsync(new LspRequest { Action = LspAction.Symbols, Query = "Greeter" });

        var status = await workspace.CallAsync(new LspRequest { Action = LspAction.Status });

        status
            .Should()
            .Contain($"\nproject not loaded: {BROKEN_TEST_PROJECT}: ")
            .And.NotContain($"project not loaded: {CONTRACT_TEST_PROJECT}");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
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
                File = CONSUMER_TEST_FILE,
                Line = 9_999,
                Character = 1,
            }
        );

        markdown.Should().BeError(ErrorCodes.INVALID_REQUEST);
        missing.Should().BeError(ErrorCodes.INVALID_REQUEST);
        pastTheEnd.Should().BeError(ErrorCodes.INVALID_REQUEST);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ReportsAPositionBesideANameInsteadOfAnEmptyResultAsync()
    {
        // In `    string Greet(string name);`, 25 is on name and 30 on the semicolon
        const int GREET_LINE = 5;
        const int NAME_CHARACTER = 25;
        const int SEMICOLON_CHARACTER = 30;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var beside = await workspace.CallRawAsync(
            new LspRequest
            {
                Action = LspAction.References,
                File = CONTRACT_TEST_FILE,
                Line = GREET_LINE,
                Character = SEMICOLON_CHARACTER,
            }
        );
        var unused = await workspace.CallRawAsync(
            new LspRequest
            {
                Action = LspAction.References,
                File = CONTRACT_TEST_FILE,
                Line = GREET_LINE,
                Character = NAME_CHARACTER,
            }
        );

        beside.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        unused.Should().BeSuccessful();
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ReportsNoSymbolForACharacterPastTheEndOfTheLineAsync()
    {
        // Counter.cs line 6 is empty, character 17 falls on Step of line 7, and the file ends on line 25
        const string COUNTER_FILE = "Application/Counter.cs";
        const int EMPTY_LINE = 6;
        const int NEXT_LINE_CHARACTER = 17;
        const int LAST_LINE = 25;
        const int PAST_THE_LAST_LINE = 500;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var pastTheLineEnd = new LspRequest
        {
            Action = LspAction.References,
            File = COUNTER_FILE,
            Line = EMPTY_LINE,
            Character = NEXT_LINE_CHARACTER,
        };

        var references = await workspace.CallRawAsync(pastTheLineEnd);
        var reads = await workspace.CallRawAsync(pastTheLineEnd with { Action = LspAction.Reads });
        var writes = await workspace.CallRawAsync(
            pastTheLineEnd with
            {
                Action = LspAction.Writes,
            }
        );
        var pastTheFileEnd = await workspace.CallRawAsync(
            pastTheLineEnd with
            {
                Line = LAST_LINE,
                Character = PAST_THE_LAST_LINE,
            }
        );

        references.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        reads.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        writes.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        pastTheFileEnd.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task TellsAMissedSymbolFromARejectedNameOnRenameAsync()
    {
        // In `namespace Sample.Contracts;`, 18 is on Contracts and 28 past the semicolon
        const int NAMESPACE_CHARACTER = 18;
        const int LINE_END_CHARACTER = 28;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var beside = await workspace.CallRawAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                File = CONTRACT_TEST_FILE,
                Line = 1,
                Character = LINE_END_CHARACTER,
                NewName = "Other",
            }
        );
        var dotted = await workspace.CallRawAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                File = CONTRACT_TEST_FILE,
                Line = 1,
                Character = NAMESPACE_CHARACTER,
                NewName = "Other.Contracts",
            }
        );

        beside.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        dotted.Should().BeError(ErrorCodes.INVALID_REQUEST);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsUnusedCodeWithTheUnusedCodeThatUsesItAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        // Checks references an xunit assembly, so it is a test project
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            overlay: UNUSED_FIXTURE
        );

        var unused = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Symbols,
                File = $"{{{LEFTOVERS_TEST_FILE},{LEFTOVER_CHECKS_TEST_FILE}}}",
                Unused = true,
            }
        );

        // Shout keeps its static class alive, Orphan stands for its member, and a test helper used by a test stays
        unused
            .Should()
            .Be(
                $"""
                {LEFTOVERS_TEST_FILE}:5:23 method Sample.Application.Leftovers.Entry()
                {LEFTOVERS_TEST_FILE}:7:23 method Sample.Application.Leftovers.Live()
                  via Sample.Application.Leftovers.Entry()
                {LEFTOVERS_TEST_FILE}:11:23 method Sample.Application.Leftovers.Tested()
                  tests only
                {LEFTOVERS_TEST_FILE}:17:21 class Sample.Application.Orphan
                2 files checked, 4 unused symbols, 4 skipped as framework code
                reflection or code outside the workspace may still use a public symbol
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task PagesUnusedCodeWithTheNamesOfUsersOnOtherPagesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            overlay: UNUSED_FIXTURE
        );

        var page = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Symbols,
                File = Path.GetFileName(LEFTOVERS_TEST_FILE),
                Unused = true,
                Limit = 1,
                Offset = 1,
            }
        );

        page.Should()
            .Be(
                $"""
                resolved file: {LEFTOVERS_TEST_FILE}
                {LEFTOVERS_TEST_FILE}:7:23 method Sample.Application.Leftovers.Live()
                  via Sample.Application.Leftovers.Entry()
                1 file checked, 4 unused symbols, 2 skipped as framework code
                truncated: 4 unused symbols in total, next offset: 2. Raise limit or narrow the query.
                reflection or code outside the workspace may still use a public symbol
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task KeepsTheMembersThatADebuggerDisplayOrAFixedLayoutReachesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(
            cancellationToken,
            overlay: UNUSED_KEPT_FIXTURE
        );

        var unused = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Symbols,
                File = KEPT_TEST_FILE,
                Unused = true,
            }
        );

        // A DebuggerDisplay format names Inner, Label and both Count overloads, an interop layout holds
        // Magic and Length, and enum members are out of the check. Only Dump, used in inactive code, is left.
        unused
            .Should()
            .Be(
                $"""
                {KEPT_TEST_FILE}:33:26 method Sample.Application.Tracing.Dump()
                1 file checked, 1 unused symbol
                reflection or code outside the workspace may still use a public symbol
                """
            );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ResolvesExtensionBlockMembersByTheirClassAsync()
    {
        const string EXTENSIONS_FILE = "Application/GreetingExtensions.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(
            EXTENSIONS_FILE,
            "namespace Sample.Application;\n\npublic static class GreetingExtensions\n{\n"
                + "    extension(string name)\n    {\n"
                + "        public string Shout() => name.ToUpperInvariant();\n    }\n}\n"
        );
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = EXTENSIONS_FILE }
        );

        var definition = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "GreetingExtensions.Shout" }
        );

        Locations(definition).Should().Equal(EXTENSIONS_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
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

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsSameNamedTypesOfDifferentProjectsAsCandidatesAsync()
    {
        const string SOURCE =
            "namespace Sample.Application;\n\ninternal static class Program\n{\n    public static void Run() { }\n}\n";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        await workspace.WriteFileAsync(APPLICATION_PROGRAM_TEST_FILE, SOURCE);
        await workspace.WriteFileAsync(CONTRACTS_PROGRAM_TEST_FILE, SOURCE);
        foreach (var file in new[] { APPLICATION_PROGRAM_TEST_FILE, CONTRACTS_PROGRAM_TEST_FILE })
        {
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.Diagnostics, File = file }
            );
        }

        var ambiguous = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Program.Run" }
        );
        ambiguous.Should().BeError(ErrorCodes.AMBIGUOUS_SYMBOL);
        Candidates(ambiguous)
            .Select(FileOf)
            .Should()
            .BeEquivalentTo([APPLICATION_PROGRAM_TEST_FILE, CONTRACTS_PROGRAM_TEST_FILE]);

        var resolved = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Definition,
                Symbol = "Program.Run",
                File = CONTRACTS_PROGRAM_TEST_FILE,
            }
        );
        resolved.Should().HaveSingleLocation(CONTRACTS_PROGRAM_TEST_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsAnExplicitImplementationBesideItsSameNamedMemberAsCandidatesAsync()
    {
        const string BAG_FILE = "Application/Bag.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var dispose = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Bag.Dispose" }
        );
        var hook = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Bag.Hook" }
        );

        dispose.Should().BeError(ErrorCodes.AMBIGUOUS_SYMBOL);
        Candidates(dispose)
            .Select(line => line.Split(' ')[0])
            .Should()
            .Equal($"{BAG_FILE}:7:17", $"{BAG_FILE}:9:22");
        hook.Should().BeSuccessful();
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task NavigatesAndRenamesBySymbolNameAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var references = await workspace.CallAsync(
            new LspRequest { Action = LspAction.References, Symbol = "IGreeter.Greet" }
        );
        Locations(references)
            .Should()
            .Contain(CONSUMER_TEST_FILE)
            .And.NotContain(OTHER_GREETER_TEST_FILE);

        var ambiguous = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Greet" }
        );
        ambiguous.Should().BeError(ErrorCodes.AMBIGUOUS_SYMBOL);
        var candidates = Candidates(ambiguous).Select(SymbolName).ToArray();
        candidates.Should().HaveCount(3);
        var contract = candidates.Should().ContainSingle(name => name.Contains("IGreeter")).Which;
        var resolved = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = contract }
        );
        resolved.Should().HaveSingleLocation(CONTRACT_TEST_FILE);

        var preview = await workspace.CallAsync(
            new LspRequest
            {
                Action = LspAction.Rename,
                Symbol = "Consumer.Run",
                NewName = "Execute",
            }
        );
        PreviewFiles(preview).Should().Equal(CONSUMER_TEST_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task MisspelledSymbolListsACandidateThatResolvesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var unknown = await workspace.CallRawAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = "Greter" }
        );

        unknown.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        var candidate = Candidates(unknown)
            .Select(SymbolName)
            .Should()
            .ContainSingle(name => name.EndsWith(".Greeter", StringComparison.Ordinal))
            .Which;
        var resolved = await workspace.CallAsync(
            new LspRequest { Action = LspAction.Definition, Symbol = candidate }
        );
        resolved.Should().HaveSingleLocation(IMPLEMENTATION_TEST_FILE);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
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

        misspelled.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        Candidates(misspelled)
            .Select(SymbolName)
            .Should()
            .Contain(name => name.EndsWith(".Counter.Total", StringComparison.Ordinal))
            .And.OnlyContain(name => name.Contains(".Counter.", StringComparison.Ordinal));
        unrelated.Should().BeError(ErrorCodes.SYMBOL_NOT_FOUND);
        Candidates(unrelated).Should().BeEmpty();
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task PagesTheReferencesOfAFieldWithoutOverlapAsync()
    {
        const int PAGE_SIZE = 3;
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);

        var all = ReferenceLocations(
            await workspace.CallAsync(
                new LspRequest { Action = LspAction.References, Symbol = TOTAL_TEST_SYMBOL }
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
                        Symbol = TOTAL_TEST_SYMBOL,
                        Limit = PAGE_SIZE,
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

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task BulkReportsAnAmbiguousFileOnlyInTheSectionThatNamesItAsync()
    {
        const string SOURCE = "namespace Sample;\n\ninternal static class Program\n{\n}\n";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await StartRoslynAsync(workspace);
        foreach (var file in new[] { APPLICATION_PROGRAM_TEST_FILE, CONTRACTS_PROGRAM_TEST_FILE })
        {
            await workspace.WriteFileAsync(file, SOURCE);
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
                    new LspRequest { Action = LspAction.Writes, Symbol = TOTAL_TEST_SYMBOL },
                    new LspRequest { Action = LspAction.Reads, Symbol = TOTAL_TEST_SYMBOL },
                    new LspRequest
                    {
                        Action = LspAction.References,
                        File = PROGRAM_FILE_NAME,
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
            .Where(section => section.Contains(ErrorCodes.AMBIGUOUS_FILE, StringComparison.Ordinal))
            .Should()
            .ContainSingle(response)
            .Which.Should()
            .Contain($"references {PROGRAM_FILE_NAME}");
        sections
            .Where(section => section.Contains("error ", StringComparison.Ordinal))
            .Should()
            .ContainSingle();
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task RenamesFilesOnlyWhenRoslynAdvertisesSupportAsync()
    {
        const string DESTINATION = "Application/RenamedGreeter.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await workspace.CallAsync(
            new LspRequest { Action = LspAction.Symbols, File = IMPLEMENTATION_TEST_FILE }
        );
        var request = new LspRequest
        {
            Action = LspAction.RenameFile,
            File = IMPLEMENTATION_TEST_FILE,
            NewName = DESTINATION,
        };

        var capabilities = JsonNode.Parse(
            await workspace.CallAsync(new LspRequest { Action = LspAction.Capabilities })
        );
        if (capabilities?["workspace"]?["fileOperations"]?["willRename"] is null)
        {
            var original = await workspace.ReadFileAsync(IMPLEMENTATION_TEST_FILE);
            var rejected = await workspace.CallRawAsync(request);
            rejected.Should().BeError(ErrorCodes.INVALID_REQUEST);
            (await workspace.ReadFileAsync(IMPLEMENTATION_TEST_FILE)).Should().Be(original);
            File.Exists(workspace.FilePath(DESTINATION)).Should().BeFalse();
            TestContext.Current.TestOutputHelper!.WriteLine(
                "Roslyn did not advertise file rename support. Refusal preserved the source."
            );
            return;
        }

        var preview = await workspace.CallAsync(request);
        File.Exists(workspace.FilePath(IMPLEMENTATION_TEST_FILE)).Should().BeTrue();
        File.Exists(workspace.FilePath(DESTINATION)).Should().BeFalse();

        var applied = await workspace.CallAsync(ApplyProposal(LspAction.RenameFile, preview));
        applied.Should().BeApplied();
        File.Exists(workspace.FilePath(IMPLEMENTATION_TEST_FILE)).Should().BeFalse();
        File.Exists(workspace.FilePath(DESTINATION)).Should().BeTrue();

        var implementation = await workspace.CallAsync(
            await workspace.AtAsync(LspAction.Implementation, CONTRACT_TEST_FILE, "Greet(")
        );
        implementation.Should().HaveSingleLocation(DESTINATION);
        TestContext.Current.TestOutputHelper!.WriteLine(
            "Roslyn advertised file rename support. Preview, apply and navigation to the new path passed."
        );
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task AppliesRenameAcrossProjectsWithoutRenamingAHomonymAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var unrelatedBefore = await workspace.ReadFileAsync(OTHER_GREETER_TEST_FILE);
        var request = await workspace.AtAsync(LspAction.Rename, CONTRACT_TEST_FILE, "Greet(");
        request = request with { NewName = "Welcome" };

        var preview = await workspace.CallAsync(request);
        var original = await workspace.ReadFileAsync(CONTRACT_TEST_FILE);
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

        foreach (
            var file in new[] { CONTRACT_TEST_FILE, IMPLEMENTATION_TEST_FILE, CONSUMER_TEST_FILE }
        )
        {
            var content = await workspace.ReadFileAsync(file);
            content.Should().Contain("Welcome(");
            content.Should().NotContain("Greet(");
        }

        var unrelatedAfter = await workspace.ReadFileAsync(OTHER_GREETER_TEST_FILE);
        unrelatedAfter.Should().Be(unrelatedBefore);
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task RejectsRenamePreviewAfterAnExternalEditAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = await workspace.AtAsync(LspAction.Rename, CONTRACT_TEST_FILE, "Greet(");
        request = request with { NewName = "Welcome" };

        var preview = await workspace.CallAsync(request);
        var original = await workspace.ReadFileAsync(CONSUMER_TEST_FILE);
        var changed = original.Replace("world", "friend", StringComparison.Ordinal);
        await workspace.WriteFileAsync(CONSUMER_TEST_FILE, changed);

        var rejected = await workspace.CallRawAsync(ApplyProposal(LspAction.Rename, preview));
        rejected.Should().BeError(ErrorCodes.STALE_EDIT);
        (await workspace.ReadFileAsync(CONSUMER_TEST_FILE)).Should().Be(changed);
        var contract = await workspace.ReadFileAsync(CONTRACT_TEST_FILE);
        contract.Should().Contain("Greet(");
        contract.Should().NotContain("Welcome(");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
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
        foreach (
            var file in new[] { CONTRACT_TEST_FILE, IMPLEMENTATION_TEST_FILE, CONSUMER_TEST_FILE }
        )
        {
            (await workspace.ReadFileAsync(file)).Should().Contain("Welcome(");
        }
    }

    [Theory(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    [InlineData(APPLY_FROM_PREVIEW)]
    [InlineData(APPLY_FROM_LISTING)]
    [InlineData(APPLY_WITHOUT_PROPOSAL)]
    public async Task ResolvesAndAppliesRoslynCodeActionAsync(string mode)
    {
        const string TITLE = "using System.Collections.Generic;";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = await workspace.AtAsync(
            LspAction.CodeActions,
            PROBLEMS_TEST_FILE,
            "List<string>"
        );
        var listing = await workspace.CallAsync(request);
        request = request with
        {
            ActionIndex = ActionIndex(listing, line =>
                line.EndsWith(" " + TITLE, StringComparison.Ordinal)
            ),
        };

        var preview = await workspace.CallAsync(request);
        (await DiagnosticsAsync(workspace)).Should().Contain(item => item.Code == "CS0246");

        var applyRequest = mode switch
        {
            APPLY_FROM_LISTING => ApplyProposal(LspAction.CodeActions, listing) with
            {
                ActionIndex = request.ActionIndex,
            },
            APPLY_FROM_PREVIEW => ApplyProposal(LspAction.CodeActions, preview),
            _ => request with { Apply = true },
        };
        var applied = await workspace.CallAsync(applyRequest);
        applied.Should().BeApplied();

        Diagnostics(applied).Should().Contain(item => item.Code == "CS8603");
        Diagnostics(applied).Should().NotContain(item => item.Code == "CS0246");

        if (mode is not APPLY_WITHOUT_PROPOSAL)
        {
            (await workspace.CallRawAsync(applyRequest))
                .Should()
                .BeError(ErrorCodes.INVALID_REQUEST);
        }

        var text = await workspace.ReadFileAsync(PROBLEMS_TEST_FILE);
        text.Should().Contain(TITLE);

        var diagnostics = await DiagnosticsAsync(workspace);
        diagnostics.Should().NotContain(item => item.Code == "CS0246");
        diagnostics.Should().Contain(item => item.Code == "CS8603");
    }

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task ListsSuppressionsOnlyOnRequestAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        var request = new LspRequest
        {
            Action = LspAction.CodeActions,
            File = PROBLEMS_TEST_FILE,
            Diagnostic = "CS0246",
        };

        var hidden = Lines(await workspace.CallAsync(request));
        var all = Lines(await workspace.CallAsync(request with { All = true }));

        var suppressions = all.Where(line =>
                line.Contains(CodeActionsHandler.SUPPRESSION_GROUP, StringComparison.Ordinal)
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

    [Fact(Explicit = true, Timeout = TEST_TIMEOUT_MILLISECONDS)]
    public async Task FixesEveryOccurrenceOfADiagnosticInADocumentAsync()
    {
        const string FILE = "Application/Usings.cs";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var workspace = await RoslynTestWorkspace.CreateAsync(cancellationToken);
        await workspace.WriteFileAsync(
            FILE,
            "using System.IO;\nusing System.Text;\n\nnamespace Sample.Application;\n\npublic static class Usings;\n"
        );
        var request = new LspRequest
        {
            Action = LspAction.CodeActions,
            File = FILE,
            Diagnostic = "IDE0005",
        };

        var listing = await workspace.CallAsync(request);
        var applied = await workspace.CallAsync(
            ApplyProposal(LspAction.CodeActions, listing) with
            {
                ActionIndex = ActionIndex(listing, line =>
                    line.Contains("Fix All", StringComparison.Ordinal)
                    && line.EndsWith(" (Document)", StringComparison.Ordinal)
                ),
            }
        );

        applied.Should().BeApplied();
        (await workspace.ReadFileAsync(FILE)).Should().NotContain("using");
    }
}
