// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using RoslynBridge.Lsp;

namespace RoslynBridge;

/// <summary>
/// Checks that Roslyn can start and prints the command that registers the bridge in each agent.
/// </summary>
internal static class Setup
{
    internal const string Command = "setup";
    internal const string ServerName = "roslyn";
    private static readonly TimeSpan _versionTimeout = TimeSpan.FromSeconds(5);

    internal static readonly Agent[] Agents =
    [
        new("Claude Code", "claude", "claude mcp add --scope user {0} -- {1}"),
        new("Codex", "codex", "codex mcp add {0} -- {1}"),
        new("GitHub Copilot CLI", "copilot", "copilot mcp add {0} -- {1}"),
        new("pi", "pi", "pi mcp add {0} -- {1}"),
        new("Antigravity", "agy", "agy mcp add {0} {1}"),
    ];

    public static int Run(TextWriter output)
    {
        var locator = RoslynLocator.Current;
        string root;
        try
        {
            root = locator.FindDotnetRoot(explicitRoot: null);
        }
        catch (DirectoryNotFoundException exception)
        {
            output.WriteLine(exception.Message);
            return 1;
        }

        output.WriteLine($".NET: {root}");
        var server = FindOrInstallServer(locator, root, output);
        if (server is null)
        {
            return 1;
        }

        output.WriteLine($"Roslyn: {server}");
        output.WriteLine();

        var bridge = Environment.ProcessPath!;
        var detected = Agents.Where(static agent => IsInstalled(agent.Executable)).ToArray();
        output.WriteLine(
            detected.Length == 0
                ? "No agent detected. Register the bridge with the command for your agent:"
                : "Register the bridge in each agent you use:"
        );
        foreach (var agent in detected.Length == 0 ? Agents : detected)
        {
            output.WriteLine();
            output.WriteLine($"  {agent.Name}");
            output.WriteLine($"  {agent.Registration(bridge)}");
        }

        return 0;
    }

    private static string? FindOrInstallServer(
        RoslynLocator locator,
        string root,
        TextWriter output
    )
    {
        try
        {
            return locator.FindServer(explicitPath: null);
        }
        catch (FileNotFoundException)
        {
            output.WriteLine("Installing the Roslyn language server as a global .NET tool...");
        }

        var startInfo = new ProcessStartInfo(Path.Combine(root, "dotnet"))
        {
            UseShellExecute = false,
        };
        foreach (
            var argument in (string[])
                ["tool", "update", "--global", RoslynLocator.ServerName, "--prerelease"]
        )
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var install = Process.Start(startInfo);
        install?.WaitForExit();
        try
        {
            return locator.FindServer(explicitPath: null);
        }
        catch (FileNotFoundException exception)
        {
            output.WriteLine(exception.Message);
            return null;
        }
    }

    // A command on PATH can be a broken version manager shim, so the agent must answer --version
    private static bool IsInstalled(string executable)
    {
        try
        {
            // Windows agents installed through npm are .cmd scripts, which only cmd.exe can run
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", executable, "--version" },
                }
                : new ProcessStartInfo(executable) { ArgumentList = { "--version" } };
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(_versionTimeout))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    internal sealed record Agent(string Name, string Executable, string Template)
    {
        public string Registration(string bridge) =>
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Template,
                ServerName,
                bridge.Contains(' ', StringComparison.Ordinal) ? $"\"{bridge}\"" : bridge
            );
    }
}
