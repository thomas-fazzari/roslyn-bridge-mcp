// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RoslynBridge.Common;
using RoslynBridge.Settings;
using RoslynBridge.Tests.Assertions;
using static RoslynBridge.Tests.Unit.TestHelpers;

namespace RoslynBridge.Tests.Integration.Roslyn;

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

    /// <param name="overlay">A fixture directory whose files replace or add to the workspace fixture.</param>
    public static async Task<RoslynTestWorkspace> CreateAsync(
        CancellationToken cancellationToken,
        int requestTimeoutSeconds = 30,
        string? settings = null,
        string? overlay = null
    )
    {
        var workspace = new RoslynTestWorkspace(
            Directory.CreateTempSubdirectory("roslyn-bridge-mcp-tests-"),
            cancellationToken
        );
        try
        {
            workspace.CopyFixture("Workspace");
            if (overlay is not null)
                workspace.CopyFixture(overlay);

            if (settings is not null)
                await workspace.WriteFileAsync(ProjectSettings.FileName, settings);

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
        var (tool, arguments) = request.ToolCall();
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
            Character = offset - prefix.LastIndexOf('\n', StringComparison.Ordinal),
        };
    }

    public async Task<int> StopAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        var process = _process
            ?? throw new InvalidOperationException("The MCP host is not started.");
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

    private void CopyFixture(string fixture)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture);
        foreach (var file in Directory.EnumerateFiles(source, "*.txt", SearchOption.AllDirectories))
        {
            var destination = FilePath(Path.GetRelativePath(source, file)[..^4]);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

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
        _process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _directory.FullName,
                ArgumentList =
                {
                    typeof(ToolPipeline).Assembly.Location,
                    LaunchArguments.WorkspaceArgument,
                    _directory.FullName,
                    LaunchArguments.StartupTimeoutArgument,
                    "60",
                    LaunchArguments.RequestTimeoutArgument,
                    requestTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        var server = Environment.GetEnvironmentVariable("ROSLYN_BRIDGE_TEST_SERVER");
        if (!string.IsNullOrWhiteSpace(server))
        {
            _process.StartInfo.ArgumentList.Add(LaunchArguments.ServerArgument);
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
