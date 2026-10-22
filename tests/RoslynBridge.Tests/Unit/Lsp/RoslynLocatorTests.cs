// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using RoslynBridge.Lsp;

namespace RoslynBridge.Tests.Unit.Lsp;

public sealed class RoslynLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-locator-").FullName;
    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);

    private static string ArchitectureVariable =>
        $"DOTNET_ROOT_{RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()}";

    [Fact]
    public void ExplicitRootWinsOverTheEnvironment()
    {
        var explicitRoot = CreateDotnetRoot("explicit");
        _environment["DOTNET_ROOT"] = CreateDotnetRoot("environment");

        Locator().FindDotnetRoot(explicitRoot).Should().Be(explicitRoot);
    }

    [Fact]
    public void InvalidExplicitRootIsRejected()
    {
        var root = CreateDotnetRoot("runtime-only");
        Directory.Delete(Path.Combine(root, "sdk"), recursive: true);

        FluentActions.Invoking(Find).Should().Throw<DirectoryNotFoundException>();

        string Find() => Locator().FindDotnetRoot(root);
    }

    [Fact]
    public void ArchitectureVariableWinsOverDotnetRoot()
    {
        _environment[ArchitectureVariable] = CreateDotnetRoot("architecture");
        _environment["DOTNET_ROOT"] = CreateDotnetRoot("environment");

        Locator()
            .FindDotnetRoot(explicitRoot: null)
            .Should()
            .Be(_environment[ArchitectureVariable]);
    }

    [Fact]
    public void InvalidDotnetRootFallsThroughToDotnetOnPath()
    {
        _environment["DOTNET_ROOT"] = Path.Combine(_root, "missing");
        var root = CreateDotnetRoot("path");
        _environment["PATH"] = root;

        Locator().FindDotnetRoot(explicitRoot: null).Should().Be(root);
    }

    [Fact]
    public void SymlinkedDotnetOnPathResolvesToItsInstall()
    {
        var root = CreateDotnetRoot("install");
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(bin, Dotnet), Path.Combine(root, Dotnet));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        _environment["PATH"] = bin;

        Locator().FindDotnetRoot(explicitRoot: null).Should().Be(root);
    }

    [Fact]
    public void FixedRootsAreTriedInOrder()
    {
        var root = CreateDotnetRoot("fixed");

        Locator(Path.Combine(_root, "missing"), root)
            .FindDotnetRoot(explicitRoot: null)
            .Should()
            .Be(root);
    }

    [Fact]
    public void MissingDotnetInstallIsReported()
    {
        FluentActions.Invoking(Find).Should().Throw<DirectoryNotFoundException>();

        string Find() => Locator(Path.Combine(_root, "missing")).FindDotnetRoot(explicitRoot: null);
    }

    [Fact]
    public void ServerOnPathWinsOverDotnetTools()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        var server = CreateFile(Path.Combine(bin, Server));
        CreateFile(Path.Combine(_root, ".dotnet", "tools", Server));
        _environment["PATH"] = bin;

        Locator().FindServer(explicitPath: null).Should().Be(server);
    }

    [Fact]
    public void ServerFallsBackToDotnetTools()
    {
        var server = CreateFile(Path.Combine(_root, ".dotnet", "tools", Server));

        Locator().FindServer(explicitPath: null).Should().Be(server);
    }

    [Fact]
    public void SymlinkedToolLaunchesTheServerBinaryBesideIt()
    {
        var tool = CreateFile(Path.Combine(_root, "store", Server));
        var server = CreateFile(
            Path.Combine(_root, "store", Executable(RoslynLocator.ServerBinaryName))
        );
        var bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        try
        {
            File.CreateSymbolicLink(Path.Combine(bin, Server), tool);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links needs privileges on this machine.");
        }

        _environment["PATH"] = bin;

        Locator().FindServer(explicitPath: null).Should().Be(server);
    }

    [Fact]
    public void MissingServerIsReported()
    {
        FluentActions.Invoking(Find).Should().Throw<FileNotFoundException>();

        string Find() => Locator().FindServer(explicitPath: null);
    }

    [Fact]
    public void ChildEnvironmentUsesOnlyTheResolvedRoot()
    {
        var root = CreateDotnetRoot("resolved");
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = "/usr/bin",
            ["DOTNET_ROOT"] = "/stale",
            [ArchitectureVariable] = "/stale",
        };

        RoslynLocator.ApplyDotnetRoot(environment, root);
        RoslynLocator.ApplyDotnetRoot(environment, root);

        environment.Should().NotContainKey(ArchitectureVariable);
        environment["DOTNET_ROOT"].Should().Be(root);
        environment["PATH"].Should().Be($"{root}{Path.PathSeparator}/usr/bin");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Dotnet => Executable("dotnet");

    private static string Server => Executable(RoslynLocator.ServerName);

    private static string Executable(string name) =>
        OperatingSystem.IsWindows() ? $"{name}.exe" : name;

    private RoslynLocator Locator(params string[] fixedRoots) =>
        new(name => _environment.GetValueOrDefault(name), _root, fixedRoots);

    private string CreateDotnetRoot(string name)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(root, "host", "fxr", "10.0.0"));
        Directory.CreateDirectory(Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.0"));
        Directory.CreateDirectory(Path.Combine(root, "sdk", "10.0.100"));
        CreateFile(Path.Combine(root, Dotnet));
        return root;
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
