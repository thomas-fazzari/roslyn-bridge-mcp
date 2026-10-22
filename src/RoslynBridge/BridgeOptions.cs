// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;

namespace RoslynBridge;

internal sealed record BridgeOptions
{
    internal const string WorkspaceArgument = "--workspace";
    internal const string ServerArgument = "--server";
    internal const string DotnetRootArgument = "--dotnet-root";
    internal const string StartupTimeoutArgument = "--startup-timeout";
    internal const string RequestTimeoutArgument = "--request-timeout";

    internal const int DefaultStartupTimeoutSeconds = 120;
    internal const int DefaultRequestTimeoutSeconds = 60;
    private const int MinimumTimeoutSeconds = 1;
    private const int MaximumTimeoutSeconds = 600;

    public string WorkspaceRoot { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>The Roslyn server executable, or <see langword="null" /> to find it.</summary>
    public string? ServerPath { get; init; }

    /// <summary>The .NET install that runs Roslyn, or <see langword="null" /> to find it.</summary>
    public string? DotnetRoot { get; init; }

    public TimeSpan StartupTimeout { get; init; } =
        TimeSpan.FromSeconds(DefaultStartupTimeoutSeconds);

    public TimeSpan RequestTimeout { get; init; } =
        TimeSpan.FromSeconds(DefaultRequestTimeoutSeconds);

    public static BridgeOptions Parse(string[] args)
    {
        var options = new BridgeOptions();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {args[index]}.", nameof(args));
            }

            var value = args[index + 1];
            options = args[index] switch
            {
                WorkspaceArgument => options with { WorkspaceRoot = Path.GetFullPath(value) },
                ServerArgument => options with { ServerPath = value },
                DotnetRootArgument => options with { DotnetRoot = Path.GetFullPath(value) },
                StartupTimeoutArgument => options with { StartupTimeout = ParseTimeout(value) },
                RequestTimeoutArgument => options with { RequestTimeout = ParseTimeout(value) },
                _ => throw new ArgumentException($"Unknown option: {args[index]}.", nameof(args)),
            };
        }

        return !Directory.Exists(options.WorkspaceRoot)
            ? throw new DirectoryNotFoundException(options.WorkspaceRoot)
            : options;
    }

    private static TimeSpan ParseTimeout(string value)
    {
        if (
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < MinimumTimeoutSeconds or > MaximumTimeoutSeconds
        )
        {
            throw new ArgumentException(
                $"Timeout must be between {MinimumTimeoutSeconds} and {MaximumTimeoutSeconds} seconds.",
                nameof(value)
            );
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
