// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace RoslynBridge.Lsp;

/// <summary>
/// Finds the Roslyn server and the .NET install it runs on, because MCP clients often launch the
/// bridge without the user's DOTNET_ROOT or shell PATH.
/// </summary>
/// <param name="fixedRoots">.NET install locations to try after the environment and PATH, in order.</param>
internal sealed class RoslynLocator(
    Func<string, string?> environment,
    string home,
    IReadOnlyList<string> fixedRoots
)
{
    internal const string ServerName = "roslyn-language-server";
    private const string RuntimeMajorVersion = "10.";
    private static readonly TimeSpan _listSdksTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] _architectureVariables =
    [
        "DOTNET_ROOT_ARM64",
        "DOTNET_ROOT_X64",
        "DOTNET_ROOT_X86",
        "DOTNET_ROOT(x86)",
    ];

    public static RoslynLocator Current { get; } =
        new(
            Environment.GetEnvironmentVariable,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            SystemRoots()
        );

    private static string Executable(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.exe" : name;

    private static string ArchitectureName =>
        RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();

    public string FindServer(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return explicitPath;
        }

        var toolsHome = environment("DOTNET_CLI_HOME") ?? home;
        return FindOnPath(ServerName)
            ?? ExistingFile(Path.Combine(toolsHome, ".dotnet", "tools", Executable(ServerName)))
            ?? throw new FileNotFoundException(
                $"The Roslyn language server was not found on PATH or in ~/.dotnet/tools. Install it with `dotnet tool install -g {ServerName} --prerelease`, or pass --server."
            );
    }

    public string FindDotnetRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
        {
            return IsValidRoot(explicitRoot)
                ? explicitRoot
                : throw new DirectoryNotFoundException(
                    $"--dotnet-root {explicitRoot} does not contain a .NET 10 runtime and an SDK."
                );
        }

        var candidates = new List<string>();
        foreach (var variable in (string[])[$"DOTNET_ROOT_{ArchitectureName}", "DOTNET_ROOT"])
        {
            if (environment(variable) is { Length: > 0 } root)
            {
                candidates.Add(root);
            }
        }

        if (FindOnPath("dotnet") is { } dotnet)
        {
            candidates.Add(Path.GetDirectoryName(RealPath(dotnet))!);
        }

        foreach (var root in candidates.Concat(fixedRoots))
        {
            if (IsValidRoot(root))
            {
                return root;
            }
        }

        // A version manager shim such as mise's resolves the install only when it runs
        return RootFromListedSdks()
            ?? throw new DirectoryNotFoundException(
                $"No .NET install with a 10.0 runtime and an SDK was found in {string.Join(", ", candidates.Concat(fixedRoots))}. Pass --dotnet-root."
            );
    }

    /// <summary>
    /// Points the Roslyn process at <paramref name="root"/> without changing the bridge's own environment.
    /// </summary>
    public static void ApplyDotnetRoot(IDictionary<string, string?> childEnvironment, string root)
    {
        foreach (var variable in _architectureVariables)
        {
            childEnvironment.Remove(variable);
        }

        childEnvironment["DOTNET_ROOT"] = root;
        var path = childEnvironment.TryGetValue("PATH", out var value) ? value : null;
        if (!string.Equals(path?.Split(Path.PathSeparator)[0], root, StringComparison.Ordinal))
        {
            childEnvironment["PATH"] = string.IsNullOrEmpty(path)
                ? root
                : $"{root}{Path.PathSeparator}{path}";
        }
    }

    internal static bool IsValidRoot(string root)
    {
        var runtimes = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        var sdks = Path.Combine(root, "sdk");
        return File.Exists(Path.Combine(root, Executable("dotnet")))
            && Directory.Exists(Path.Combine(root, "host", "fxr"))
            && Directory.Exists(runtimes)
            && Directory
                .EnumerateDirectories(runtimes)
                .Any(static runtime =>
                    Path.GetFileName(runtime)
                        .StartsWith(RuntimeMajorVersion, StringComparison.Ordinal)
                )
            && Directory.Exists(sdks)
            && Directory.EnumerateDirectories(sdks).Any();
    }

    private string? FindOnPath(string name)
    {
        var path = environment("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => ExistingFile(Path.Combine(directory, Executable(name))))
            .FirstOrDefault(static file => file is not null);
    }

    private static string? ExistingFile(string path) => File.Exists(path) ? path : null;

    private static string RealPath(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    // `dotnet --list-sdks` prints one "10.0.100 [/path/to/dotnet/sdk]" line per SDK
    private string? RootFromListedSdks()
    {
        if (FindOnPath("dotnet") is not { } dotnet)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo(dotnet, "--list-sdks")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(_listSdksTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        if (!process.WaitForExit(_listSdksTimeout))
        {
            process.Kill(entireProcessTree: true);
            return null;
        }

        return output
            .Result.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => line.Trim())
            .Where(static line => line.EndsWith(']'))
            .Select(static line => line[(line.IndexOf('[', StringComparison.Ordinal) + 1)..^1])
            .Select(Path.GetDirectoryName)
            .FirstOrDefault(static root => root is not null && IsValidRoot(root));
    }

    private static string[] SystemRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            roots.AddRange(RegisteredWindowsRoot());
            roots.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "dotnet"
                )
            );
        }
        else
        {
            var architecture = ArchitectureName.ToLowerInvariant();
            foreach (
                var file in (string[])
                    [$"/etc/dotnet/install_location_{architecture}", "/etc/dotnet/install_location"]
            )
            {
                if (File.Exists(file))
                {
                    roots.Add(File.ReadAllText(file).Trim());
                }
            }

            roots.Add(OperatingSystem.IsMacOS() ? "/usr/local/share/dotnet" : "/usr/share/dotnet");
        }

        var miseData =
            Environment.GetEnvironmentVariable("MISE_DATA_DIR")
            ?? Path.Combine(home, ".local", "share", "mise");
        roots.AddRange(
            Path.Combine(home, ".dotnet"),
            Path.Combine(miseData, "dotnet-root"),
            "/opt/homebrew/opt/dotnet/libexec",
            "/usr/local/opt/dotnet/libexec",
            "/usr/lib/dotnet"
        );
        return [.. roots];
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegisteredWindowsRoot()
    {
        using var machine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry32
        );
        using var key = machine.OpenSubKey(
            $@"SOFTWARE\dotnet\Setup\InstalledVersions\{ArchitectureName.ToLowerInvariant()}"
        );
        return key?.GetValue("InstallLocation") is string location ? [location] : [];
    }
}
