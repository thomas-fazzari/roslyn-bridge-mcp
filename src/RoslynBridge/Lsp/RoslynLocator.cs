// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;

namespace RoslynBridge.Lsp;

/// <summary>
/// Finds the Roslyn server and the .NET install it runs on.
/// </summary>
/// <param name="bridgeRoot">The .NET install that runs the bridge, used unless --dotnet-root names another.</param>
internal sealed class RoslynLocator(
    Func<string, string?> environment,
    string home,
    string bridgeRoot
)
{
    internal const string ServerName = "roslyn-language-server";
    internal const string ServerBinaryName = "Microsoft.CodeAnalysis.LanguageServer";

    private const string RuntimeMajorVersion = "10.";

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
            BridgeRoot()
        );

    private static string Executable(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.exe" : name;

    // The runtime directory is ROOT/shared/Microsoft.NETCore.App/VERSION
    internal static string BridgeRoot() =>
        Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));

    public string FindServer(string? explicitPath)
    {
        var toolsHome = environment("DOTNET_CLI_HOME") ?? home;
        var launcher =
            explicitPath
            ?? FindOnPath(ServerName)
            ?? ExistingFile(Path.Combine(toolsHome, ".dotnet", "tools", Executable(ServerName)))
            ?? throw new FileNotFoundException(
                $"The Roslyn language server was not found on PATH or in ~/.dotnet/tools. Install it with `dotnet tool install -g {ServerName} --prerelease`, or pass --server."
            );
        var server = Path.Combine(
            Path.GetDirectoryName(RealPath(launcher))!,
            Executable(ServerBinaryName)
        );
        return File.Exists(server) ? server : launcher;
    }

    public string FindDotnetRoot(string? explicitRoot)
    {
        var root = explicitRoot ?? bridgeRoot;
        return IsValidRoot(root)
            ? root
            : throw new DirectoryNotFoundException(
                $"The .NET install {root} does not contain a .NET 10 runtime and an SDK. Pass --dotnet-root."
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
}
