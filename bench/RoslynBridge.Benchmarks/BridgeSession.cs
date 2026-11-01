// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace RoslynBridge.Benchmarks;

/// <summary>
/// Bridge process driven over MCP, as an agent drives it.
/// </summary>
internal sealed class BridgeSession : IAsyncDisposable
{
    internal const string ExecutableVariable = "ROSLYN_BRIDGE_BENCH_EXECUTABLE";
    internal const string RepositoriesVariable = "ROSLYN_BRIDGE_BENCH_REPOS";
    internal const string LogsVariable = "ROSLYN_BRIDGE_BENCH_LOGS";

    private static readonly TimeSpan _loadTimeout = TimeSpan.FromMinutes(10);

    // Startup precision (a status call takes about 1 ms)
    private static readonly TimeSpan _statusInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan _exitTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] _logNameOptions = ["--benchmarkName", "--job"];

    private Process? _process;
    private McpClient? _client;
    private TextWriter? _log;

    private McpClient Client =>
        _client ?? throw new InvalidOperationException("The bridge is not connected.");

    /// <summary>
    /// Returns once Roslyn has loaded every project.
    /// </summary>
    public async Task StartAsync(string repository)
    {
        using var timeout = new CancellationTokenSource(_loadTimeout);
        await ConnectAsync(repository, timeout.Token);
        await WaitUntilLoadedAsync(timeout.Token);
    }

    /// <summary>
    /// Calls a tool and returns its text.
    /// </summary>
    /// <remarks>
    /// Throws on a tool error, so a broken build cannot look fast.
    /// </remarks>
    public Task<string> CallAsync(string tool, params (string Name, object? Value)[] arguments) =>
        CallAsync(tool, arguments, CancellationToken.None);

    /// <summary>
    /// Calls a tool that must fail with this error code.
    /// </summary>
    /// <remarks>
    /// Throws otherwise, so the timed call stays on the error path.
    /// </remarks>
    public async Task<string> FailAsync(
        string errorCode,
        string tool,
        params (string Name, object? Value)[] arguments
    )
    {
        var (failed, text) = await SendAsync(tool, arguments, CancellationToken.None);
        return failed && text.StartsWith($"error {errorCode}:", StringComparison.Ordinal)
            ? text
            : throw new InvalidOperationException($"{tool} did not fail with {errorCode}: {text}");
    }

    internal static string RequiredVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Set {name}. mise run bench:run sets it.");

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_process is null)
        {
            return;
        }

        if (!_process.HasExited)
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(_exitTimeout);
            try
            {
                await _process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _process.Kill(entireProcessTree: true);
            }
        }

        _process.Dispose();
        if (_log is not null)
        {
            await _log.DisposeAsync();
        }
    }

    private async Task ConnectAsync(string repository, CancellationToken cancellationToken)
    {
        var workspace = Path.Combine(RequiredVariable(RepositoriesVariable), repository);
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(RequiredVariable(ExecutableVariable))
            {
                ArgumentList = { "--workspace", workspace },
                WorkingDirectory = workspace,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        _log = OpenLog();
        if (_log is { } log)
        {
            _process.ErrorDataReceived += (_, line) =>
                log.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DateTime.UtcNow:HH:mm:ss.fff} {line.Data}"
                    )
                );
        }

        _process.Start();
        // Drain logs (a full pipe blocks the bridge)
        _process.BeginErrorReadLine();
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(
                _process.StandardInput.BaseStream,
                _process.StandardOutput.BaseStream
            ),
            cancellationToken: cancellationToken
        );
    }

    private async Task WaitUntilLoadedAsync(CancellationToken cancellationToken)
    {
        var status = await CallAsync("lsp", [("action", "status")], cancellationToken);
        while (status.Contains("loading: true", StringComparison.Ordinal))
        {
            await Task.Delay(_statusInterval, cancellationToken);
            status = await CallAsync("lsp", [("action", "status")], cancellationToken);
        }

        // The bridge starts loading before it answers, so a load that stopped without a server failed
        if (!status.Contains("running: true", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Roslyn failed to load the workspace. The bridge log is in {LogsVariable}."
            );
        }
    }

    private async Task<string> CallAsync(
        string tool,
        (string Name, object? Value)[] arguments,
        CancellationToken cancellationToken
    )
    {
        var (failed, text) = await SendAsync(tool, arguments, cancellationToken);
        return failed ? throw new InvalidOperationException($"{tool} failed: {text}") : text;
    }

    private async Task<(bool Failed, string Text)> SendAsync(
        string tool,
        (string Name, object? Value)[] arguments,
        CancellationToken cancellationToken
    )
    {
        var result = await Client.CallToolAsync(
            tool,
            arguments.ToDictionary(
                argument => argument.Name,
                argument => argument.Value,
                StringComparer.Ordinal
            ),
            cancellationToken: cancellationToken
        );
        var text = string.Concat(
            result.Content.OfType<TextContentBlock>().Select(block => block.Text)
        );
        return (result.IsError is true, text);
    }

    // Named after the benchmark and job that BenchmarkDotNet passes on the command line
    private static StreamWriter? OpenLog()
    {
        if (Environment.GetEnvironmentVariable(LogsVariable) is not { Length: > 0 } directory)
        {
            return null;
        }

        var arguments = Environment.GetCommandLineArgs();
        var name = string.Join(
            '-',
            _logNameOptions
                .Select(option => Array.IndexOf(arguments, option) + 1)
                .Where(index => index > 0 && index < arguments.Length)
                .Select(index => arguments[index])
                .Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
        );
        Directory.CreateDirectory(directory);
        return new StreamWriter(Path.Combine(directory, $"{name}.log"), append: true)
        {
            AutoFlush = true,
        };
    }
}
