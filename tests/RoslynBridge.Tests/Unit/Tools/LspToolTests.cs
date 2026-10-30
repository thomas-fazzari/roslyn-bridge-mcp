// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using RoslynBridge.Changes;
using RoslynBridge.Lsp;
using RoslynBridge.Output;
using RoslynBridge.Requests;
using RoslynBridge.Settings;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tests.Fakes;
using RoslynBridge.Tools;
using StreamJsonRpc;
using static RoslynBridge.Tests.Fakes.LspLocationJson;
using static RoslynBridge.Tests.Fakes.LspSymbolJson;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Unit.Tools;

public sealed class LspToolTests : IDisposable
{
    private const string SourceFile = "Source.cs";
    private const string SourceText = "class A {}\nclass B {}\nclass C {}\nclass D {}\n";

    private const int Error = (int)DiagnosticSeverity.Error;
    private const int Warning = (int)DiagnosticSeverity.Warning;
    private const int Hint = (int)DiagnosticSeverity.Hint;
    private const string ReportedCode = "CS0168";

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
                        return Task.FromResult<JsonNode?>(FakeDiagnosticServer.FullReport([]));
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

    [Fact]
    public async Task DiagnosticWithoutARangeIsReportedAsInvalidLspDataAsync()
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (_, _) =>
                    Task.FromResult<JsonNode?>(
                        FakeDiagnosticServer.FullReport([new JsonObject { ["message"] = "Broken" }])
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Diagnostics, File = "A.cs" },
            TestCt
        );

        response.Should().BeError(ErrorCodes.Lsp);
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
                    Task.FromResult<JsonNode?>(FakeDiagnosticServer.FullReport([]))
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
    public async Task RepeatedDiagnosticsReuseTheReportThatRoslynKeepsUnchangedAsync()
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        var sent = new ConcurrentQueue<string?>();
        await using var session = CreateSession(
            _root,
            FakeDiagnosticServer.WithResultIds(ReportedCode, sent)
        );
        using var tool = CreateTool(session);
        var request = new LspRequest { Action = LspAction.Diagnostics, File = "A.cs" };

        var first = ResponseText(await tool.ExecuteAsync(request, TestCt));
        var second = ResponseText(await tool.ExecuteAsync(request, TestCt));

        first.Should().Contain(ReportedCode);
        second.Should().Be(first);
        sent.Should().Equal(null, FakeDiagnosticServer.ResultId);
    }

    [Fact]
    public async Task ReloadForgetsTheResultIdsOfTheStoppedServerAsync()
    {
        await WriteFileAsync(_root, "A.cs", "class A {}");
        var sent = new ConcurrentQueue<string?>();
        await using var session = CreateSession(
            _root,
            FakeDiagnosticServer.WithResultIds(ReportedCode, sent)
        );
        using var tool = CreateTool(session);
        var request = new LspRequest { Action = LspAction.Diagnostics, File = "A.cs" };

        await tool.ExecuteAsync(request, TestCt);
        await tool.ExecuteAsync(new LspRequest { Action = LspAction.Reload }, TestCt);
        var afterReload = ResponseText(await tool.ExecuteAsync(request, TestCt));

        afterReload.Should().Contain(ReportedCode);
        sent.Should().Equal(null, null);
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
                            GreeterSymbol("Z.cs", 0),
                            GreeterSymbol("A.cs", 2),
                            GreeterSymbol("A.cs", 0)
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

                    return Found(string.Create(CultureInfo.InvariantCulture, $"Found{line}.cs"));
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
            FakeSymbolServer.With(
                workspace: new(StringComparer.Ordinal)
                {
                    ["Calculator"] = [WorkspaceSymbol("Calculator", path)],
                    ["Calculater"] =
                    [
                        WorkspaceSymbol("Calculator", path),
                        WorkspaceSymbol("Zebra", Path.Combine(_root, "Zebra.cs")),
                    ],
                },
                documents: new(StringComparer.Ordinal)
                {
                    ["Calculator.cs"] = [DocumentSymbol("Calculator")],
                }
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
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Calculater"] = [WorkspaceSymbol("Calculator", Path.Combine(_root, "Gone.cs"))],
            }
        );

        var response = await CallAsync(server, SourceOf("Calculater"));

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response).Split('\n').Should().ContainSingle();
    }

    [Fact]
    public async Task QualifiedNameWithAnUnknownContainerNamesSimilarTypesWithoutCandidatesAsync()
    {
        var path = await WriteFileAsync(_root, "Nopes.cs", "class Nopes\n{\n}\n");
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Missing"] = [MethodSymbol("MissingFile", path)],
                ["Nope"] = [WorkspaceSymbol("Nopes", path), MethodSymbol("Nope", path)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["Nopes.cs"] = [DocumentSymbol("Nopes", DocumentMethod("MissingFile()"))],
            }
        );

        var response = await CallAsync(server, ReferencesOf("Nope.Missing"));

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResultLines
            .Lines(ResponseText(response))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Contain("Nopes")
            .And.NotContain("MissingFile");
    }

    [Fact]
    public async Task ConstructorsHaveTheConstructorKindInSymbolsMembersAndSourceAsync()
    {
        var box = await WriteFileAsync(
            _root,
            "Box.cs",
            """
            class Box
            {
                public Box(int value) {}
            }
            """
        );
        var constructor = WorkspaceSymbol(
            "Box",
            box,
            LspSymbolKinds.Method,
            MethodLine,
            MethodCharacter
        );
        constructor["containerName"] = "in Box (project App)";
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method switch
                        {
                            LspMethods.WorkspaceSymbol => new JsonArray(
                                WorkspaceSymbol("Box", box),
                                constructor.DeepClone()
                            ),
                            LspMethods.TextDocumentDocumentSymbol => new JsonArray(
                                DocumentSymbol("Box", DocumentMethod("Box(int)"))
                            ),
                            _ => null,
                        }
                    )
            )
        );
        using var tool = CreateTool(session);

        var document = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, File = "Box.cs" },
            TestCt
        );
        var workspace = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Symbols, Query = "Box" },
            TestCt
        );
        var members = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Members, Symbol = "Box" },
            TestCt
        );
        var source = await tool.ExecuteAsync(
            new LspRequest { Action = LspAction.Source, Symbol = "Box.Box(int)" },
            TestCt
        );

        ResponseText(document).Should().Contain(" constructor Box(int)");
        ResponseText(workspace).Should().Contain("Box.cs:3:12 constructor Box in Box");
        ResponseText(members).Should().Contain(" constructor Box(int)");
        ResponseText(source).Should().Contain(" constructor Box.Box(int)");
    }

    [Fact]
    public async Task QualifiedNameWithAKnownContainerListsOnlyItsMembersAsync()
    {
        var calculator = await WriteFileAsync(_root, "Calculator.cs", "class Calculator {}\n");
        var other = await WriteFileAsync(_root, "Other.cs", "class Other {}\n");
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Missing"] = [MethodSymbol("MissingA", other)],
                ["Calculator"] = [WorkspaceSymbol("Calculator", calculator)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["Calculator.cs"] =
                [
                    DocumentSymbol("Calculator", DocumentMethod("MissingTotalValue()")),
                ],
                ["Other.cs"] = [DocumentSymbol("Other", DocumentMethod("MissingA()"))],
            }
        );

        var response = await CallAsync(server, ReferencesOf("Calculator.Missing"));

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response)
            .Should()
            .Contain("'Calculator' has no member 'Missing'")
            .And.NotContain("Other.MissingA()");
        CandidateNames(response).Should().Equal("Calculator.MissingTotalValue()");
    }

    [Fact]
    public async Task QualifiedNameWithAKnownContainerAndNoSimilarMemberPointsToMembersAsync()
    {
        var calculator = await WriteFileAsync(_root, "Calculator.cs", "class Calculator {}\n");
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Calculator"] = [WorkspaceSymbol("Calculator", calculator)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["Calculator.cs"] = [DocumentSymbol("Calculator", DocumentMethod("Add()"))],
            }
        );

        var response = await CallAsync(server, ReferencesOf("Calculator.Missing"));

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response)
            .Should()
            .Be(
                "error symbol_not_found: No declaration matches 'Calculator.Missing' because 'Calculator' has no member 'Missing'. Use members to list its members."
            );
    }

    [Fact]
    public async Task SimilarTypesDeclaredInSeveralFilesAreNamedOnceAsync()
    {
        var first = await WriteFileAsync(_root, "Nopes.One.cs", "partial class Nopes {}\n");
        var second = await WriteFileAsync(_root, "Nopes.Two.cs", "partial class Nopes {}\n");
        var part = DocumentSymbol("Nopes", DocumentMethod("MissingFile()"));
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Missing"] = [MethodSymbol("MissingFile", first)],
                ["Nope"] = [WorkspaceSymbol("Nopes", first), WorkspaceSymbol("Nopes", second)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["Nopes.One.cs"] = [part],
                ["Nopes.Two.cs"] = [part],
            }
        );

        var response = await CallAsync(server, ReferencesOf("Nope.Missing"));

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        ResponseText(response).Should().Contain("Similar types: Nopes.");
    }

    [Fact]
    public async Task QualifiedNameInANamespaceStillListsTheSimilarTypeAsync()
    {
        var path = await WriteFileAsync(_root, "Calculator.cs", "class Calculator {}\n");
        var server = FakeSymbolServer.With(
            workspace: new(StringComparer.Ordinal)
            {
                ["Calculater"] = [WorkspaceSymbol("Calculator", path)],
            },
            documents: new(StringComparer.Ordinal)
            {
                ["Calculator.cs"] = [DocumentNamespace("Sample", DocumentSymbol("Calculator"))],
            }
        );

        var response = await CallAsync(server, ReferencesOf("Sample.Calculater"));

        response.Should().BeError(ErrorCodes.SymbolNotFound);
        CandidateNames(response).Should().Equal("Sample.Calculator");
    }

    [Fact]
    public async Task CalleesListConstructorsWithTheConstructorKindAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        var box = await WriteFileAsync(_root, "Box.cs", "class Box\n{\n    public Box() {}\n}\n");
        var greeter = await WriteFileAsync(_root, "Greeter.cs", "class Greeter(string name);\n");
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, parameters) =>
                    Task.FromResult<JsonNode?>(
                        method switch
                        {
                            LspMethods.TextDocumentPrepareCallHierarchy => PreparedItem(),
                            LspMethods.CallHierarchyOutgoingCalls => new JsonArray(
                                OutgoingCall("Box.Box()", box, MethodLine, MethodCharacter),
                                OutgoingCall("Greeter.Greeter(string)", greeter, 0, 6)
                            ),
                            LspMethods.TextDocumentDocumentSymbol => parameters!["textDocument"]![
                                "uri"
                            ]!
                                .GetValue<string>()
                                .EndsWith("Box.cs", StringComparison.Ordinal)
                                ? new JsonArray(DocumentSymbol("Box", DocumentMethod("Box()")))
                                : new JsonArray(DocumentSymbol("Greeter")),
                            _ => null,
                        }
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Callees,
                File = SourceFile,
                Line = 1,
                Character = 1,
            },
            TestCt
        );

        ResultLines
            .Lines(ResponseText(response))
            .Where(ResultLines.IsFileLine)
            .Should()
            .Equal("Box.cs:3:12 constructor Box.Box()", "Greeter.cs:1:7 constructor Greeter");
    }

    [Theory]
    [InlineData("callers", 2)]
    [InlineData("callees", 1)]
    [InlineData("supertypes", 1)]
    [InlineData("subtypes", 1)]
    public async Task EmptyHierarchyResultNamesWhatIsMissingAsync(string action, int lineCount)
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method
                            is LspMethods.TextDocumentPrepareCallHierarchy
                                or LspMethods.TextDocumentPrepareTypeHierarchy
                            ? PreparedItem()
                            : null
                    )
            )
        );
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = Enum.Parse<LspAction>(action, ignoreCase: true),
                File = SourceFile,
                Line = 1,
                Character = 1,
            },
            TestCt
        );

        var lines = ResultLines.Lines(ResponseText(response));
        lines[0].Should().Be($"no {action}");
        lines.Should().HaveCount(lineCount);
    }

    [Fact]
    public async Task HierarchyWithoutAPreparedItemReportsNoSymbolsAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session);

        var response = await tool.ExecuteAsync(
            new LspRequest
            {
                Action = LspAction.Callers,
                File = SourceFile,
                Line = 1,
                Character = 1,
            },
            TestCt
        );

        ResponseText(response).Should().Be("no symbols");
    }

    [Fact]
    public async Task CodeActionListingHidesSuppressionsButKeepsTheIndexesAsync()
    {
        await WriteFileAsync(_root, SourceFile, SourceText);
        await using var session = CreateSession(
            _root,
            FakeServer(server =>
                server.Handle = (method, _) =>
                    Task.FromResult<JsonNode?>(
                        method is LspMethods.TextDocumentCodeAction
                            ? FixesAroundASuppressionGroup()
                            : null
                    )
            )
        );
        using var tool = CreateTool(session);
        var request = new LspRequest
        {
            Action = LspAction.CodeActions,
            File = SourceFile,
            Line = 1,
            Character = 1,
        };

        var hidden = ResultLines.Lines(ResponseText(await tool.ExecuteAsync(request, TestCt)));
        var all = ResultLines.Lines(
            ResponseText(await tool.ExecuteAsync(request with { All = true }, TestCt))
        );

        hidden.Skip(1).SkipLast(1).Should().Equal("0 quickfix Remove it", "4 quickfix Add it");
        hidden[^1].Should().StartWith("3 ");
        all.Skip(1)
            .Should()
            .Equal(
                "0 quickfix Remove it",
                $"1 {LspChanges.SuppressionGroup} > Suppress X > in Source",
                $"2 {LspChanges.SuppressionGroup} > Configure X severity > Error",
                $"3 {LspChanges.SuppressionGroup} > Configure X severity > Warning",
                "4 quickfix Add it"
            );
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

    [Fact]
    public async Task BulkReportsAnAmbiguousFileInTheSectionOfItsQueryAsync()
    {
        await WriteFileAsync(_root, "A/Calculator.cs", SourceText);
        await WriteFileAsync(_root, "B/Calculator.cs", SourceText);
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
                    File = "Calculator.cs",
                },
                ReferencesAt(2) with
                {
                    File = "Source.cs",
                }
            ),
            TestCt
        );

        response.Should().BeSuccessful();
        ResponseText(response)
            .Should()
            .Be(
                $"{ResultText.ResolvedFilePrefix}src/Math/Source.cs\n"
                    + "## 1 references Calculator.cs:1:1\n"
                    + $"error {ErrorCodes.AmbiguousFile}: 'Calculator.cs' matches 2 files. Send more of the path. Matches: A/Calculator.cs, B/Calculator.cs\n"
                    + "## 2 references src/Math/Source.cs:2:1\nFound.cs 1:1"
            );
    }

    [Fact]
    public async Task ReloadReadsTheSettingsAgainAndKeepsThemWhenTheFileIsInvalidAsync()
    {
        const int firstSeconds = 20;
        const int secondSeconds = 40;

        var settingsPath = Path.Combine(_root, ProjectSettings.FileName);
        var arguments = LaunchArguments.Parse([LaunchArguments.WorkspaceArgument, _root]);

        await File.WriteAllTextAsync(settingsPath, StartupTimeoutSetting(firstSeconds), TestCt);
        var settings = new SettingsMonitor(() =>
            BridgeOptions.From(arguments, ProjectSettingsReader.Read(_root))
        );
        await using var session = CreateSession(_root, FakeServer());
        using var tool = CreateTool(session, settings);
        var reload = new LspRequest { Action = LspAction.Reload };

        await File.WriteAllTextAsync(settingsPath, StartupTimeoutSetting(secondSeconds), TestCt);
        var reloaded = await tool.ExecuteAsync(reload, TestCt);
        var afterReload = settings.CurrentValue.StartupTimeout;
        await File.WriteAllTextAsync(settingsPath, "{ \"unknown\": 1 }", TestCt);
        var invalid = await tool.ExecuteAsync(reload, TestCt);

        reloaded.Should().BeSuccessful();
        afterReload.Should().Be(TimeSpan.FromSeconds(secondSeconds));
        invalid.Should().BeError(ErrorCodes.InvalidSettings);
        ResponseText(invalid).Should().Contain("unknown");
        settings.CurrentValue.StartupTimeout.Should().Be(TimeSpan.FromSeconds(secondSeconds));
    }

    [Fact]
    public async Task DiagnosticsUseTheProjectSeverityWhenTheCallGivesNoneAsync()
    {
        const string hintCode = "IDE0005";
        await WriteFileAsync(_root, "A.cs", "class A {}");
        var options = new BridgeOptions
        {
            WorkspaceRoot = _root,
            DiagnosticsSeverity = DiagnosticSeverity.Hint,
        };
        await using var session = CreateSession(
            _root,
            DiagnosticServer(("A.cs", 0, 0, Error, "CS0246"), ("A.cs", 1, 0, Hint, hintCode))
        );
        using var tool = CreateTool(session);
        var withProjectSeverity = new DiagnosticsTool(tool, Monitor(options));
        var withBuiltInSeverity = new DiagnosticsTool(
            tool,
            Monitor(options with { DiagnosticsSeverity = null })
        );

        var projectDefault = await withProjectSeverity.DiagnosticsAsync(
            "A.cs",
            cancellationToken: TestCt
        );
        var explicitWarning = await withProjectSeverity.DiagnosticsAsync(
            "A.cs",
            DiagnosticSeverity.Warning,
            cancellationToken: TestCt
        );
        var builtInDefault = await withBuiltInSeverity.DiagnosticsAsync(
            "A.cs",
            cancellationToken: TestCt
        );

        ResponseText(projectDefault).Should().Contain(hintCode);
        ResponseText(explicitWarning).Should().NotContain(hintCode);
        ResponseText(builtInDefault).Should().NotContain(hintCode);
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
                                ["range"] = Range(diagnostic.Line, diagnostic.Character),
                                ["severity"] = diagnostic.Severity,
                                ["code"] = diagnostic.Code,
                                ["message"] = $"Message {diagnostic.Code}",
                            }
                        );
                    }
                }

                return Task.FromResult<JsonNode?>(FakeDiagnosticServer.FullReport(items));
            }
        );

    private JsonArray FiveReferences() =>
        new([
            .. Enumerable
                .Range(0, 5)
                .Select(line => Location(Path.Combine(_root, "Found.cs"), line)),
        ]);

    private JsonObject GreeterSymbol(string file, int line) =>
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

    private JsonArray Found(string file) => new(Location(Path.Combine(_root, file)));

    // Each file name is long enough that two of these sections cannot share a response
    private JsonArray LargeFound() =>
        new([
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

    private static JsonArray PreparedItem() =>
        new(new JsonObject { ["name"] = "A.Run()", ["kind"] = LspSymbolKinds.Method });

    private static JsonObject OutgoingCall(string name, string path, int line, int character) =>
        new()
        {
            ["to"] = new JsonObject
            {
                ["name"] = name,
                ["kind"] = LspSymbolKinds.Method,
                ["uri"] = new Uri(path).AbsoluteUri,
                ["selectionRange"] = Range(line, character),
            },
            ["fromRanges"] = new JsonArray(),
        };

    private static JsonArray FixesAroundASuppressionGroup()
    {
        static JsonObject Fix(string title) =>
            new()
            {
                ["title"] = title,
                ["kind"] = "quickfix",
                ["data"] = new JsonObject { ["CodeActionPath"] = new JsonArray(title) },
            };

        static JsonObject Suppression(string title, params string[] path) =>
            new()
            {
                ["title"] = title,
                ["data"] = new JsonObject
                {
                    ["CodeActionPath"] = new JsonArray([
                        .. path.Prepend(LspChanges.SuppressionGroup).Select(part => (JsonNode)part),
                    ]),
                },
            };

        return
        [
            Fix("Remove it"),
            new JsonObject
            {
                ["title"] = LspChanges.SuppressionGroup,
                ["kind"] = "quickfix",
                ["data"] = new JsonObject
                {
                    ["NestedCodeActions"] = new JsonArray(
                        Suppression("in Source", "Suppress X", "in Source"),
                        Suppression("Error", "Configure X severity", "Error"),
                        Suppression("Warning", "Configure X severity", "Warning")
                    ),
                },
            },
            Fix("Add it"),
        ];
    }

    private async Task<CallToolResult> CallAsync(RoslynLauncher server, LspRequest request)
    {
        await using var session = CreateSession(_root, server);
        using var tool = CreateTool(session);
        return await tool.ExecuteAsync(request, TestCt);
    }

    private static LspRequest ReferencesOf(string symbol) =>
        new() { Action = LspAction.References, Symbol = symbol };

    private static string[] CandidateNames(CallToolResult response) =>
        [
            .. ResultLines
                .Lines(ResponseText(response))
                .Skip(1)
                .Select(line => line.Split(' ', 3)[2]),
        ];

    private LspTool CreateTool(RoslynSession session, SettingsMonitor settings) =>
        TestHelpers.CreateTool(_root, session, settings: settings);

    private static string StartupTimeoutSetting(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{{ \"startupTimeout\": {seconds} }}");

    private LspTool CreateTool(RoslynSession session, BridgeOptions? options = null) =>
        TestHelpers.CreateTool(_root, session, options);
}
