// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RoslynBridge.Requests;
using RoslynBridge.Tests.Assertions;
using RoslynBridge.Tools;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Integration.Roslyn;

/// <summary>
/// Runs the built MCP host against an isolated copy of a real C# solution.
/// </summary>
/// <remarks>
/// Set ROSLYN_BRIDGE_TEST_EXECUTABLE to an absolute executable path to test a published bridge.
/// Otherwise, the fixture launches the built assembly with dotnet.
/// Set ROSLYN_BRIDGE_TEST_SERVER to select an installed Roslyn server by its executable path.
/// </remarks>
internal sealed class RoslynTestWorkspace : IAsyncDisposable
{
    private readonly DirectoryInfo _directory;
    private readonly CancellationToken _cancellationToken;
    private Process? _process;
    private Task<string>? _standardError;
    private McpClient? _client;

    private RoslynTestWorkspace(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        _directory = directory;
        _cancellationToken = cancellationToken;
    }

    public McpClient Client =>
        _client ?? throw new InvalidOperationException("The MCP client is not connected.");

    public static async Task<RoslynTestWorkspace> CreateAsync(
        CancellationToken cancellationToken,
        int requestTimeoutSeconds = 30
    )
    {
        var workspace = new RoslynTestWorkspace(
            Directory.CreateTempSubdirectory("roslyn-bridge-mcp-tests-"),
            cancellationToken
        );
        try
        {
            workspace.CopyFixture();
            await workspace.RestoreAsync();
            await workspace.ConnectAsync(requestTimeoutSeconds);
            return workspace;
        }
        catch
        {
            await workspace.DisposeAsync();
            throw;
        }
    }

    public string FilePath(string relativePath) => Path.Combine(_directory.FullName, relativePath);

    public Task<string> ReadFileAsync(string relativePath) =>
        File.ReadAllTextAsync(FilePath(relativePath), _cancellationToken);

    public Task WriteFileAsync(string relativePath, string text) =>
        File.WriteAllTextAsync(FilePath(relativePath), text, _cancellationToken);

    public async Task<string> CallAsync(LspRequest request)
    {
        var response = await CallRawAsync(request);
        response.Should().BeSuccessful();
        return ResponseText(response);
    }

    public async Task<CallToolResult> CallRawAsync(LspRequest request)
    {
        var (tool, arguments) = ToolCall(request);
        return await Client.CallToolAsync(tool, arguments, cancellationToken: _cancellationToken);
    }

    public async Task<LspRequest> AtAsync(LspAction action, string file, string symbol)
    {
        var text = await File.ReadAllTextAsync(FilePath(file), _cancellationToken);
        var offset = text.IndexOf(symbol, StringComparison.Ordinal);
        offset.Should().BeGreaterThanOrEqualTo(0);
        var prefix = text[..offset];
        return new LspRequest
        {
            Action = action,
            File = file,
            Line = prefix.Count(character => character == '\n') + 1,
            Character = offset - prefix.LastIndexOf('\n'),
        };
    }

    /// <summary>Closes the host input and waits for the host to exit.</summary>
    /// <returns>The exit code of the host.</returns>
    public async Task<int> StopAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        var process =
            _process ?? throw new InvalidOperationException("The MCP host is not started.");
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException exception)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(
                "The MCP host did not stop after its input was closed.",
                exception
            );
        }

        TestContext.Current.TestOutputHelper?.WriteLine(await _standardError!);
        return process.ExitCode;
    }

    // Disposal only logs a failed stop, so it cannot replace the failure of the test
    public async ValueTask DisposeAsync()
    {
        var output = TestContext.Current.TestOutputHelper;
        try
        {
            if (_process is { HasExited: false })
            {
                var exitCode = await StopAsync();
                output?.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The MCP host exited with code {exitCode}."
                    )
                );
            }
            else if (_client is not null)
            {
                await _client.DisposeAsync();
            }
        }
        catch (TimeoutException exception)
        {
            output?.WriteLine(exception.Message);
        }
        finally
        {
            _process?.Dispose();
            _directory.Delete(recursive: true);
        }
    }

    private void CopyFixture()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Workspace");
        foreach (var file in Directory.EnumerateFiles(source, "*.txt", SearchOption.AllDirectories))
        {
            var destination = FilePath(Path.GetRelativePath(source, file)[..^4]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    /// <summary>
    /// Sends a request the way an agent would: to the tool that owns its action, with flat arguments.
    /// </summary>
    private static (string Tool, Dictionary<string, object?> Arguments) ToolCall(LspRequest request)
    {
        var (tool, parameters) = request.Action switch
        {
            LspAction.Diagnostics => (
                DiagnosticsTool.Name,
                ["file", "severity", "limit", "offset"]
            ),
            LspAction.Symbols => (SymbolsTool.Name, ["file", "query", "limit", "offset"]),
            _ when request.Action.IsNavigation => (
                NavigateTool.Name,
                new[]
                {
                    "action",
                    "symbol",
                    "file",
                    "line",
                    "character",
                    "context",
                    "limit",
                    "offset",
                }
            ),
            LspAction.Bulk => (BulkNavigateTool.Name, ["queries", "context", "limit", "offset"]),
            _ when request.Action.IsChange => (
                EditTool.Name,
                [
                    "action",
                    "symbol",
                    "file",
                    "line",
                    "character",
                    "endLine",
                    "endCharacter",
                    "newName",
                    "diagnostic",
                    "actionIndex",
                    "proposalId",
                    "apply",
                    "all",
                    "limit",
                ]
            ),
            _ => (ServerTool.Name, ["action", "method", "parameters", "apply"]),
        };

        return (tool, Arguments(request, parameters));
    }

    private static Dictionary<string, object?> Arguments(LspRequest request, string[] names)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(request.Action.ToString()),
            ["file"] = request.File,
            ["symbol"] = request.Symbol,
            ["severity"] = request.Severity.ToString().ToLowerInvariant(),
            ["line"] = request.Line,
            ["character"] = request.Character,
            ["endLine"] = request.EndLine,
            ["endCharacter"] = request.EndCharacter,
            ["query"] = request.Query,
            ["newName"] = request.NewName,
            ["diagnostic"] = request.Diagnostic,
            ["actionIndex"] = request.ActionIndex,
            ["proposalId"] = request.ProposalId,
            ["method"] = request.Method,
            ["parameters"] = request.Parameters,
            ["apply"] = request.Apply ? true : null,
            ["all"] = request.All ? true : null,
            ["context"] = request.Context ? true : null,
            ["limit"] = request.Limit,
            ["offset"] = request.Offset,
            ["queries"] = request.Queries.Count > 0 ? QueryArguments(request.Queries) : null,
        };

        return names
            .Where(name => values[name] is not null)
            .ToDictionary(name => name, name => values[name], StringComparer.Ordinal);
    }

    private static List<Dictionary<string, object?>> QueryArguments(
        IEnumerable<LspRequest> queries
    ) =>
        [
            .. queries.Select(query =>
                Arguments(query, ["action", "symbol", "file", "line", "character"])
            ),
        ];

    private async Task RestoreAsync()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _directory.FullName,
            ArgumentList = { "restore", "Workspace.slnx", "--nologo", "--verbosity", "quiet" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        process.Start().Should().BeTrue();
        var output = process.StandardOutput.ReadToEndAsync(_cancellationToken);
        var error = process.StandardError.ReadToEndAsync(_cancellationToken);
        try
        {
            await process.WaitForExitAsync(_cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        process.ExitCode.Should().Be(0, await output + await error);
    }

    private async Task ConnectAsync(int requestTimeoutSeconds)
    {
        var executable = Environment.GetEnvironmentVariable("ROSLYN_BRIDGE_TEST_EXECUTABLE");
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(executable ?? "dotnet")
            {
                WorkingDirectory = _directory.FullName,
                ArgumentList =
                {
                    BridgeOptions.WorkspaceArgument,
                    _directory.FullName,
                    BridgeOptions.StartupTimeoutArgument,
                    "60",
                    BridgeOptions.RequestTimeoutArgument,
                    requestTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        if (executable is null)
        {
            _process.StartInfo.ArgumentList.Insert(0, typeof(LspTool).Assembly.Location);
        }

        var server = Environment.GetEnvironmentVariable("ROSLYN_BRIDGE_TEST_SERVER");
        if (!string.IsNullOrWhiteSpace(server))
        {
            _process.StartInfo.ArgumentList.Add(BridgeOptions.ServerArgument);
            _process.StartInfo.ArgumentList.Add(server);
        }

        _process.Start().Should().BeTrue();
        _standardError = _process.StandardError.ReadToEndAsync(CancellationToken.None);
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(
                _process.StandardInput.BaseStream,
                _process.StandardOutput.BaseStream
            ),
            cancellationToken: _cancellationToken
        );
    }
}
