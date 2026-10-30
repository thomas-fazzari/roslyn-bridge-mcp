// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using RoslynBridge.Lsp;

namespace RoslynBridge;

/// <summary>
/// Checks that Roslyn can start and prints the command that registers the bridge in each agent.
/// </summary>
internal static class Setup
{
    internal const string Command = "setup";
    internal const string McpServerName = "roslyn";
    internal const string PackageId = "ThomasFazzari.RoslynBridge";

    // The Roslyn version the CI tests the bridge against, in eng/tasks/ci/roslyn.sh
    internal const string ServerVersion = "5.12.0-1.26426.8";
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

        var bridge = BridgeCommand(Environment.ProcessPath!, root, HostRoots());
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

    /// <summary>
    /// Builds the command an agent runs, which must start without the user's shell environment.
    /// </summary>
    /// <param name="dotnetRoot">The .NET install that runs the bridge.</param>
    /// <param name="hostRoots">The installs a .NET tool launcher finds without DOTNET_ROOT.</param>
    internal static string BridgeCommand(
        string processPath,
        string dotnetRoot,
        IReadOnlyCollection<string> hostRoots
    )
    {
        var unix = !OperatingSystem.IsWindows();

        // dnx runs the package through the dotnet host, which cannot start the bridge on its own
        if (
            string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            // The dnx script runs the dotnet beside it, so its full path needs no DOTNET_ROOT
            var dnx = unix ? Quote(Path.Combine(dotnetRoot, "dnx")) : "dnx";
            return $"{dnx} {PackageId}@{Program.Version} --yes";
        }

        var launcher = Quote(processPath);
        if (!unix || hostRoots.Any(root => SamePath(root, dotnetRoot)))
        {
            return launcher;
        }

        return $"/usr/bin/env DOTNET_ROOT={Quote(dotnetRoot)} {launcher}";
    }

    // A .NET tool launcher reads the registered install location, then the default one
    private static string[] HostRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }

        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var registered = (string[])
            [$"/etc/dotnet/install_location_{architecture}", "/etc/dotnet/install_location"];
        var roots = registered
            .Where(File.Exists)
            .Select(static file => File.ReadAllText(file).Trim())
            .ToList();
        roots.Add(OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : "/usr/share/dotnet");
        return [.. roots];
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal
        );

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;

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
                ["tool", "update", "--global", RoslynLocator.ServerName, "--version", ServerVersion]
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
        public string Registration(string bridgeCommand) =>
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Template,
                McpServerName,
                bridgeCommand
            );
    }
}
