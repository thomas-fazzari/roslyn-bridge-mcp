// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text.Json.Nodes;
using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tools;
using StreamJsonRpc;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LspToolTests : IDisposable
{
    private const string SourceFile = "Source.cs";
    private const string SourceText = "class A {}\nclass B {}\nclass C {}\nclass D {}\n";
    private const int LspClassKind = 5;

    private const int Error = (int)DiagnosticSeverity.Error;
    private const int Warning = (int)DiagnosticSeverity.Warning;

    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-lsp-tool-").FullName;

    [Fact]
    public async Task BridgeBugIsNotReportedAsAnInvalidRequestAsync()
    {
        await using var session = CreateSession(
            _root,
            (_, _) => throw new InvalidOperationException("Bridge invariant failed.")
        );
        using var tool = CreateTool(session);

        await FluentActions.Awaiting(Execute).Should().ThrowAsync<InvalidOperationException>();
        return;

        Task Execute() => tool.ExecuteAsync(new LspRequest { Action = LspAction.Symbols }, TestCt);
    }

    [Theory]
    [InlineData(LspMethods.Initialize)]
    [InlineData(LspMethods.TextDocumentDidOpen)]
    [InlineData(LspMethods.WorkspaceDidChangeWatchedFiles)]
    [InlineData(LspMethods.WorkspaceApplyEdit)]
    public async Task RawRequestRefusesMethodsTheBridgeOwnsAsync(string method)
    {
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = method,
                Apply = true,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    [Theory]
    [InlineData(LspMethods.WorkspaceExecuteCommand)]
    [InlineData("custom/unlistedMethod")]
    public async Task RawRequestOutsideTheReadOnlyMethodsRequiresApplyAsync(string method)
    {
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = method,
                Parameters = new JsonObject { ["command"] = "test.command" },
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task CommandFailureAfterAWriteReportsTheAppliedFilesAsync()
    {
        var source = await WriteFileAsync(_root, "Source.cs", "class A {}");
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = async (_, _) =>
                {
                    await server.RequestClientAsync(
                        LspMethods.WorkspaceApplyEdit,
                        new JsonObject
                        {
                            ["edit"] = WorkspaceChanges(source, TextEdit(0, 6, 0, 7, "B")),
                        },
                        TestCt
                    );
                    throw new LocalRpcException("The command failed after its edit.");
                }
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Request,
                Method = LspMethods.WorkspaceExecuteCommand,
                Parameters = new JsonObject { ["command"] = "test.command" },
                Apply = true,
            },
            TestCt
        );

        response.Should().BeError(ErrorCodes.Lsp);
        ResponseText(response)
            .Should()
            .Contain("failed step: command")
            .And.Contain("applied: 1 file, 1 operation, Roslyn not synchronized")
            .And.Contain("change Source.cs");
        await AssertFileTextAsync(source, "class B {}");
    }

    [Fact]
    public async Task MissingServerExecutableIsReportedAsAnIoErrorAsync()
    {
        var options = new BridgeOptions
        {
            WorkspaceRoot = _root,
            ServerPath = Path.Combine(_root, "missing-server"),
        };
        await using var session = CreateSession(_root, RoslynProcess.Start, options);
        using var tool = CreateTool(session, options);

        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);

        response.Should().BeError(ErrorCodes.Io);
    }

    [Fact]
    public async Task CallReportsLoadingWhenTheWorkspaceLoadsPastTheStartupTimeoutAsync()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new BridgeOptions
        {
            WorkspaceRoot = _root,
            StartupTimeout = TimeSpan.FromMilliseconds(50),
        };
        await using var session = CreateSession(
            _root,
            FakeServer(server => server.ProjectLoad = load.Task),
            options
        );
        using var tool = CreateTool(session, options);

        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);

        response.Should().BeError(ErrorCodes.Loading);
        session.IsLoading.Should().BeTrue();
    }

    [Fact]
    public async Task CanceledCallLeavesTheLoadForTheNextCallAsync()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
            {
                launches++;
                server.ProjectLoad = load.Task;
                launched.SetResult();
            })
        );
        using var tool = CreateTool(session);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCt);

        var canceled = tool.ExecuteAsync(SymbolQuery(), cancellation.Token);
        await launched.Task;
        await cancellation.CancelAsync();
        await FluentActions.Awaiting(Canceled).Should().ThrowAsync<OperationCanceledException>();
        load.SetResult();
        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);

        response.Should().BeSuccessful();
        launches.Should().Be(1);
        return;

        Task Canceled() => canceled;
    }

    [Fact(Timeout = 30_000)]
    public async Task StatusAnswersWhileAnotherCallWaitsForTheLoadAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
            {
                server.ProjectLoad = load.Task;
                launched.SetResult();
            })
        );
        using var tool = CreateTool(session);

        var waiting = tool.ExecuteAsync(SymbolQuery(), cancellationToken);
        await launched.Task;
        var status = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Status },
            cancellationToken
        );
        load.SetResult();
        await waiting;

        ResponseText(status).Should().Contain("loading: true");
    }

    [Fact]
    public async Task PreloadBeginsTheLoadWithoutWaitingAndTheFirstCallUsesItAsync()
    {
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launches = 0;
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
            {
                launches++;
                server.ProjectLoad = load.Task;
            })
        );
        using var tool = CreateTool(session);

        await session.StartLoadingAsync();

        session.IsLoading.Should().BeTrue();
        load.SetResult();
        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);
        response.Should().BeSuccessful();
        launches.Should().Be(1);
    }

    [Fact]
    public async Task FileOutsideEveryProjectSinceTheLoadIsReportedWithoutWaitingAsync()
    {
        await WriteFileAsync(_root, "Loose.cs", "class Loose {}");
        var contextRequests = 0;
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                {
                    if (method is not LspMethods.TextDocumentGetProjectContexts)
                    {
                        return Task.FromResult<JsonNode?>(
                            new JsonObject { ["items"] = new JsonArray() }
                        );
                    }

                    contextRequests++;
                    return Task.FromResult<JsonNode?>(
                        new JsonObject
                        {
                            ["_vs_projectContexts"] = new JsonArray(
                                new JsonObject { ["_vs_is_miscellaneous"] = true }
                            ),
                        }
                    );
                }
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = "Loose.cs" },
            TestCt
        );

        ResponseText(response)
            .Should()
            .Contain("not in a project, no semantic diagnostics: Loose.cs");
        contextRequests.Should().Be(1);
    }

    [Theory]
    [InlineData("{A,B}.cs")]
    [InlineData("{A.cs,Sub/*.cs}")]
    public async Task DiagnosticsExpandBraceAlternativesAsync(string file)
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        await WriteFileAsync(_root, "B.cs", "class B {}");
        await WriteFileAsync(_root, "Sub/B.cs", "class C {}");
        await WriteFileAsync(_root, "Other.cs", "class D {}");
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) =>
                    Task.FromResult<JsonNode?>(new JsonObject { ["items"] = new JsonArray() })
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = file },
            TestCt
        );

        ResponseText(response).Should().StartWith("2 files checked");
    }

    [Fact]
    public async Task DiagnosticsKeepOnlyTheRequestedIdsAndSayThatTheFilterAppliedAsync()
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        await using var session = CreateSession(
            _root,
            DiagnosticServer(("A.cs", 0, 0, Error, "CS0246"), ("A.cs", 1, 0, Warning, "CS8618"))
        );
        using var tool = CreateTool(session);

        var filtered = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Diagnostics,
                File = "A.cs",
                Ids = ["cs0246"],
            },
            TestCt
        );
        var unmatched = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Diagnostics,
                File = "A.cs",
                Ids = ["CS9999"],
            },
            TestCt
        );

        ResponseText(filtered)
            .Split('\n')
            .Should()
            .Equal(
                "A.cs",
                "  1:1 error CS0246 Message CS0246",
                "1 file checked, 1 diagnostic in 1 file, filtered by ids"
            );
        ResponseText(unmatched).Should().Be("1 file checked, no diagnostics, filtered by ids");
    }

    [Fact]
    public async Task DiagnosticSummaryListsEachCodeByCountThenCodeAsync()
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        await WriteFileAsync(_root, "B.cs", "class B {}");
        await using var session = CreateSession(
            _root,
            DiagnosticServer(
                ("A.cs", 0, 2, Warning, "CS8618"),
                ("A.cs", 1, 0, Error, "CS0246"),
                ("A.cs", 3, 4, Error, "CS0246"),
                ("A.cs", 5, 0, Warning, "CS0168"),
                ("B.cs", 0, 0, Error, "CS0246"),
                ("B.cs", 1, 1, Warning, "CS0168"),
                ("B.cs", 2, 0, Warning, "CS8618"),
                ("B.cs", 3, 0, Warning, "IDE0051")
            )
        );
        using var tool = CreateTool(session);
        var request = new LspRequest { Action = LspAction.Diagnostics, Summary = true };

        var full = await tool.ExecuteAsync(request, TestCt);
        var firstPage = await tool.ExecuteAsync(request with { Limit = 2 }, TestCt);
        var secondPage = await tool.ExecuteAsync(request with { Limit = 2, Offset = 2 }, TestCt);

        ResponseText(full)
            .Split('\n')
            .Should()
            .Equal(
                "CS0246 x3 error A.cs:2:1 Message CS0246",
                "CS0168 x2 warning A.cs:6:1 Message CS0168",
                "CS8618 x2 warning A.cs:1:3 Message CS8618",
                "IDE0051 x1 warning B.cs:4:1 Message IDE0051",
                "2 files checked, 8 diagnostics in 2 files"
            );
        ResponseText(firstPage)
            .Should()
            .StartWith(
                "CS0246 x3 error A.cs:2:1 Message CS0246\nCS0168 x2 warning A.cs:6:1 Message CS0168\n2 files checked, 8 diagnostics in 2 files\ntruncated: 4 codes in total, next offset: 2."
            );
        ResponseText(secondPage).Should().StartWith("CS8618 x2 ").And.NotContain("truncated:");
    }

    [Fact]
    public async Task DiagnosticOffsetCrossesFilesInPositionOrderAndKeepsTheTotalsAsync()
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        await WriteFileAsync(_root, "B.cs", "class B {}");
        await using var session = CreateSession(
            _root,
            DiagnosticServer(
                ("A.cs", 4, 0, Error, "CS0001"),
                ("A.cs", 1, 0, Error, "CS0002"),
                ("B.cs", 0, 0, Error, "CS0003"),
                ("B.cs", 2, 0, Error, "CS0004")
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Diagnostics,
                Limit = 2,
                Offset = 1,
            },
            TestCt
        );

        ResponseText(response)
            .Should()
            .StartWith(
                "A.cs\n  5:1 error CS0001 Message CS0001\nB.cs\n  1:1 error CS0003 Message CS0003\n2 files checked, 4 diagnostics in 2 files\ntruncated: 4 diagnostics in total, next offset: 3."
            );
    }

    [Fact]
    public async Task OffsetReadsTheNextPageAndTheTruncationNamesTheNextOffsetAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) => Task.FromResult<JsonNode?>(FiveReferences())
            )
        );
        using var tool = CreateTool(session);
        var request = ReferencesAt(1) with { Limit = 2 };

        var first = await tool.ExecuteAsync(request, TestCt);
        var second = await tool.ExecuteAsync(request with { Offset = 2 }, TestCt);
        var last = await tool.ExecuteAsync(request with { Offset = 4 }, TestCt);
        var past = await tool.ExecuteAsync(request with { Offset = 9 }, TestCt);

        ResponseText(first)
            .Should()
            .StartWith("Found.cs 1:1 2:1\ntruncated: 5 locations in total, next offset: 2.");
        ResponseText(second)
            .Should()
            .StartWith("Found.cs 3:1 4:1\ntruncated: 5 locations in total, next offset: 4.");
        ResponseText(last).Should().Be("Found.cs 5:1");
        ResponseText(past).Should().Be("offset past the end: 5 locations in total");
    }

    [Fact]
    public async Task BulkAppliesItsOffsetToEveryQueryAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) => Task.FromResult<JsonNode?>(FiveReferences())
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk(ReferencesAt(1), ReferencesAt(2)) with
            {
                Limit = 1,
                Offset = 3,
            },
            TestCt
        );

        ResponseText(response)
            .Should()
            .Contain(
                "## 1 references Source.cs:1:1\nFound.cs 4:1\ntruncated: 5 locations in total, next offset: 4."
            )
            .And.Contain(
                "## 2 references Source.cs:2:1\nFound.cs 4:1\ntruncated: 5 locations in total, next offset: 4."
            );
    }

    [Fact]
    public async Task NegativeOffsetIsRejectedAsAnInvalidRequestAsync()
    {
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(SymbolQuery() with { Offset = -1 }, TestCt);

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task WorkspaceSymbolsOfTheSameRankAreOrderedByLocationAsync()
    {
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) =>
                    Task.FromResult<JsonNode?>(
                        new JsonArray(
                            WorkspaceSymbol("Z.cs", 0),
                            WorkspaceSymbol("A.cs", 2),
                            WorkspaceSymbol("A.cs", 0)
                        )
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(SymbolQuery(), TestCt);

        ResponseText(response)
            .Split('\n')
            .Should()
            .Equal("A.cs:1:1 class Greeter", "A.cs:3:1 class Greeter", "Z.cs:1:1 class Greeter");
    }

    [Fact(Timeout = 30_000)]
    public async Task BulkReportsEachQueryInRequestOrderAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteFileAsync(_root, SourceFile, SourceText);
        var othersAnswered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var answered = 0;
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = async (_, parameters) =>
                {
                    var line = LineOf(parameters);
                    // The first query finishes last, so the order cannot be the order of completion
                    if (line == 1)
                    {
                        await othersAnswered.Task;
                    }
                    else if (Interlocked.Increment(ref answered) == 2)
                    {
                        othersAnswered.SetResult();
                    }

                    return Found($"Found{line}.cs");
                }
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk(ReferencesAt(1), ReferencesAt(2), ReferencesAt(3)),
            cancellationToken
        );

        ResponseText(response)
            .Should()
            .Be(
                "## 1 references Source.cs:1:1\nFound1.cs 1:1\n"
                    + "## 2 references Source.cs:2:1\nFound2.cs 1:1\n"
                    + "## 3 references Source.cs:3:1\nFound3.cs 1:1"
            );
    }

    [Fact]
    public async Task BulkReportsAFailedQueryInItsSectionAndRunsTheOthersAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, parameters) =>
                    Task.FromResult<JsonNode?>(
                        parameters?["position"] is null ? null : Found("Found.cs")
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk(
                ReferencesAt(1),
                new LspRequest { Action = LspAction.References, Symbol = "Missing" },
                ReferencesAt(3)
            ),
            TestCt
        );

        response.Should().BeSuccessful();
        var sections = ResponseText(response).Split("\n## ");
        sections.Should().HaveCount(3);
        sections[0].Should().EndWith("Found.cs 1:1");
        sections[1].Should().StartWith($"2 references Missing\nerror {ErrorCodes.SymbolNotFound}:");
        sections[2].Should().EndWith("Found.cs 1:1");
    }

    [Fact]
    public async Task BulkResolvesASymbolOnceForEveryQueryThatNamesItAsync()
    {
        var symbolRequests = 0;
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                {
                    if (method is LspMethods.WorkspaceSymbol)
                    {
                        Interlocked.Increment(ref symbolRequests);
                    }

                    return Task.FromResult<JsonNode?>(null);
                }
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk(
                new LspRequest { Action = LspAction.References, Symbol = "Missing" },
                new LspRequest { Action = LspAction.Callers, Symbol = "Missing" }
            ),
            TestCt
        );

        var sections = ResponseText(response).Split("\n## ");
        sections
            .Should()
            .HaveCount(2)
            .And.AllSatisfy(section =>
                section.Should().Contain($"error {ErrorCodes.SymbolNotFound}:")
            );
        symbolRequests.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(LspRequest.MaximumBulkQueries + 1)]
    public async Task BulkRejectsAnEmptyOrOversizedBatchAsync(int count)
    {
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk([.. Enumerable.Repeat(ReferencesAt(1), count)]),
            TestCt
        );

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task BulkOmitsTheSectionsThatDoNotFitAndListsTheirSizesAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, parameters) =>
                    Task.FromResult<JsonNode?>(
                        LineOf(parameters) switch
                        {
                            3 => Found("Small.cs"),
                            4 => new JsonArray(),
                            _ => LargeFound(),
                        }
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk(ReferencesAt(1), ReferencesAt(2), ReferencesAt(3), ReferencesAt(4)) with
            {
                Limit = LspRequest.MaximumResultLimit,
            },
            TestCt
        );

        response.Should().BeSuccessful();
        var text = ResponseText(response);
        text.Length.Should().BeLessThanOrEqualTo(ResultText.MaximumResponseCharacters);
        text.Should()
            .Contain("## 3 references Source.cs:3:1\nSmall.cs 1:1")
            .And.Contain("## 4 references Source.cs:4:1: none");
        var omitted = text.Split('\n')
            .Single(line => line.StartsWith("omitted to fit: ", StringComparison.Ordinal));
        omitted
            .Should()
            .MatchRegex(@"^omitted to fit: 1 references Source\.cs:1:1 \([\d,]+ chars\)\. ");
        text.Should().NotContain("## 1 ");
    }

    [Fact(Timeout = 30_000)]
    public async Task BulkKeepsFinishedSectionsWhenTheTimeoutEndsTheCallAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await WriteFileAsync(_root, SourceFile, SourceText);
        var options = new BridgeOptions
        {
            WorkspaceRoot = _root,
            RequestTimeout = TimeSpan.FromSeconds(1),
        };
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, parameters) =>
                    LineOf(parameters) == 2
                        ? new TaskCompletionSource<JsonNode?>().Task
                        : Task.FromResult<JsonNode?>(Found("Found.cs"))
            ),
            options
        );
        using var tool = CreateTool(session, options);
        await tool.ExecuteAsync(Bulk(ReferencesAt(1)), cancellationToken);

        var response = await tool.ExecuteAsync(
            Bulk(ReferencesAt(1), ReferencesAt(2), ReferencesAt(3)),
            cancellationToken
        );

        response.Should().BeSuccessful();
        var sections = ResponseText(response).Split("\n## ");
        sections.Should().HaveCount(3);
        sections[0].Should().EndWith("Found.cs 1:1");
        sections[1].Should().Contain($"error {ErrorCodes.Timeout}:");
        sections[2].Should().EndWith("Found.cs 1:1");
    }

    [Fact]
    public async Task UnknownSymbolListsSimilarSymbolsThatResolveWhenSentBackAsync()
    {
        var path = await WriteFileAsync(_root, "Calculator.cs", "class Calculator\n{\n}\n");
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method switch
                        {
                            LspMethods.WorkspaceSymbol => new JsonArray(
                                WorkspaceSymbol("Calculator", path),
                                WorkspaceSymbol("Zebra", Path.Combine(_root, "Zebra.cs"))
                            ),
                            LspMethods.TextDocumentDocumentSymbol => new JsonArray(
                                DocumentSymbol("Calculator")
                            ),
                            _ => null,
                        }
                    )
            )
        );
        using var tool = CreateTool(session);

        var unknown = await tool.ExecuteAsync(SourceOf("Calculater"), TestCt);

        unknown.Should().BeError(ErrorCodes.SymbolNotFound);
        var candidate = ResponseText(unknown).Split('\n').Skip(1).Should().ContainSingle().Which;
        var name = candidate.Split(' ', 3)[2];
        var resolved = await tool.ExecuteAsync(SourceOf(name), TestCt);
        resolved.Should().BeSuccessful();
    }

    [Fact]
    public async Task UnknownSymbolStillReportsNotFoundWhenASimilarSymbolsFileIsGoneAsync()
    {
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.WorkspaceSymbol
                            ? new JsonArray(
                                WorkspaceSymbol("Calculator", Path.Combine(_root, "Gone.cs"))
                            )
                            : null
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(SourceOf("Calculater"), TestCt);

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response).Split('\n').Should().ContainSingle();
    }

    [Theory]
    [InlineData("Calculator.cs", true)]
    [InlineData("Math/Calculator.cs", true)]
    [InlineData("ator.cs", false)]
    public async Task FileNameResolvesOnlyAtWholePathSegmentsAsync(string file, bool resolves)
    {
        await WriteFileAsync(_root, "src/Math/Calculator.cs", "class Calculator {}");
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, File = file },
            TestCt
        );

        if (resolves)
        {
            ResponseText(response)
                .Split('\n')[0]
                .Should()
                .Be(ResultText.ResolvedFilePrefix + "src/Math/Calculator.cs");
            return;
        }

        response.Should().BeError(ErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task FileNameThatMatchesSeveralFilesIsAmbiguousAsync()
    {
        await WriteFileAsync(_root, "A/Calculator.cs", "class A {}");
        await WriteFileAsync(_root, "B/Calculator.cs", "class B {}");
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, File = "Calculator.cs" },
            TestCt
        );

        response.Should().BeError(ErrorCodes.AmbiguousFile);
        ResponseText(response).Should().Contain("A/Calculator.cs").And.Contain("B/Calculator.cs");
    }

    [Fact]
    public async Task BulkResolvesEachQueryFileAndEchoesTheResolvedPathOnceAsync()
    {
        await WriteFileAsync(_root, "src/Math/Source.cs", SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) => Task.FromResult<JsonNode?>(Found("Found.cs"))
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            Bulk(
                ReferencesAt(1) with
                {
                    File = "Source.cs",
                },
                ReferencesAt(2) with
                {
                    File = "Math/Source.cs",
                }
            ),
            TestCt
        );

        ResponseText(response)
            .Should()
            .Be(
                $"{ResultText.ResolvedFilePrefix}src/Math/Source.cs\n"
                    + "## 1 references src/Math/Source.cs:1:1\nFound.cs 1:1\n"
                    + "## 2 references src/Math/Source.cs:2:1\nFound.cs 1:1"
            );
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // a diagnostic is its file, zero-based line and character, LSP severity and code
    private static RoslynLauncher DiagnosticServer(
        params (string File, int Line, int Character, int Severity, string Code)[] diagnostics
    ) =>
        FakeServer(server =>
            server.Handle = (method, parameters) =>
            {
                var items = new JsonArray();
                if (method is LspMethods.TextDocumentDiagnostic)
                {
                    var uri = parameters!["textDocument"]!["uri"]!.GetValue<string>();
                    var file = Path.GetFileName(new Uri(uri).LocalPath);
                    foreach (
                        var diagnostic in diagnostics.Where(found =>
                            string.Equals(found.File, file, StringComparison.Ordinal)
                        )
                    )
                    {
                        items.Add(
                            new JsonObject
                            {
                                ["range"] = new JsonObject
                                {
                                    ["start"] = new JsonObject
                                    {
                                        ["line"] = diagnostic.Line,
                                        ["character"] = diagnostic.Character,
                                    },
                                },
                                ["severity"] = diagnostic.Severity,
                                ["code"] = diagnostic.Code,
                                ["message"] = $"Message {diagnostic.Code}",
                            }
                        );
                    }
                }

                return Task.FromResult<JsonNode?>(new JsonObject { ["items"] = items });
            }
        );

    private JsonArray FiveReferences() =>
        new([
            .. Enumerable
                .Range(0, 5)
                .Select(line => Location(Path.Combine(_root, "Found.cs"), line)),
        ]);

    private JsonObject WorkspaceSymbol(string file, int line) =>
        new()
        {
            ["name"] = "Greeter",
            ["kind"] = 5,
            ["location"] = Location(Path.Combine(_root, file), line),
        };

    private static LspRequest SymbolQuery() =>
        new() { Action = LspAction.Symbols, Query = "Greeter" };

    private static LspRequest Bulk(params LspRequest[] queries) =>
        new() { Action = LspAction.Bulk, Queries = queries };

    private static LspRequest SourceOf(string symbol) =>
        new() { Action = LspAction.Source, Symbol = symbol };

    private static LspRequest ReferencesAt(int line) =>
        new()
        {
            Action = LspAction.References,
            File = SourceFile,
            Line = line,
            Character = 1,
        };

    private static int LineOf(JsonNode? parameters) =>
        parameters!["position"]!["line"]!.GetValue<int>() + 1;

    private JsonArray Found(string file) => new JsonArray(Location(Path.Combine(_root, file)));

    // Each file name is long enough that two of these sections cannot share a response
    private JsonArray LargeFound() =>
        new JsonArray([
            .. Enumerable
                .Range(0, LspRequest.MaximumResultLimit)
                .Select(index =>
                    Location(
                        Path.Combine(
                            _root,
                            new string('d', 100),
                            string.Create(CultureInfo.InvariantCulture, $"File{index}.cs")
                        )
                    )
                ),
        ]);

    private static JsonObject Location(string path, int line = 0) =>
        new()
        {
            ["uri"] = new Uri(path).AbsoluteUri,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = line, ["character"] = 0 },
            },
        };

    // The declaration of a class named at line 1, character 7
    private static JsonObject WorkspaceSymbol(string name, string path) =>
        new()
        {
            ["name"] = name,
            ["kind"] = LspClassKind,
            ["location"] = new JsonObject
            {
                ["uri"] = new Uri(path).AbsoluteUri,
                ["range"] = new JsonObject
                {
                    ["start"] = new JsonObject { ["line"] = 0, ["character"] = 6 },
                },
            },
        };

    private static JsonObject DocumentSymbol(string name) =>
        new()
        {
            ["name"] = name,
            ["kind"] = LspClassKind,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = 0, ["character"] = 0 },
                ["end"] = new JsonObject { ["line"] = 2, ["character"] = 1 },
            },
            ["selectionRange"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = 0, ["character"] = 6 },
                ["end"] = new JsonObject { ["line"] = 0, ["character"] = 16 },
            },
        };

    private LspTool CreateTool(RoslynSession session, BridgeOptions? options = null) =>
        TestHelpers.CreateTool(_root, session, options);
}
