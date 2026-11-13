// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using RoslynBridge.Lsp;

namespace RoslynBridge.Tests.Unit;

public sealed class SetupServerTests : TempWorkspaceTests
{
    private const string OlderVersion = "5.0.0-1.25000.1";

    private readonly Dictionary<string, string> _environment = [with(StringComparer.Ordinal)];
    private readonly List<ProcessStartInfo> _commands = [];
    private readonly StringWriter _output = new();

    private static string Server =>
        OperatingSystem.IsWindows() ? $"{RoslynLocator.ServerName}.exe" : RoslynLocator.ServerName;

    private string Tools => Path.Combine(Root, ".dotnet", "tools");

    [Fact]
    public void OlderGlobalToolIsUpdatedToThePinnedVersion()
    {
        InstallGlobalTool(OlderVersion);

        FindOrInstall();

        _commands
            .Should()
            .ContainSingle()
            .Which.ArgumentList.Should()
            .Contain(["update", "--global", RoslynLocator.ServerName, Setup.ServerVersion]);
    }

    [Fact]
    public void PinnedGlobalToolIsKept()
    {
        var server = InstallGlobalTool(Setup.ServerVersion);

        FindOrInstall().Should().Be(server);
        _commands.Should().BeEmpty();
    }

    [Fact]
    public void ServerOutsideTheGlobalToolsIsKeptWithAWarning()
    {
        var bin = Path.Combine(Root, "bin");
        var server = CreateFile(Path.Combine(bin, Server));
        InstallGlobalTool(OlderVersion);
        _environment["PATH"] = bin;

        FindOrInstall().Should().Be(server);
        _commands.Should().BeEmpty();
        _output.ToString().Should().Contain(Setup.ServerVersion);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _output.Dispose();
        }

        base.Dispose(disposing);
    }

    private string? FindOrInstall() =>
        Setup.FindOrInstallServer(
            new RoslynLocator(
                name => _environment.GetValueOrDefault(name),
                Root,
                Path.Combine(Root, "missing")
            ),
            Path.Combine(Root, "dotnet"),
            _output,
            _commands.Add
        );

    private string InstallGlobalTool(string version)
    {
        Directory.CreateDirectory(Path.Combine(Tools, ".store", RoslynLocator.ServerName, version));
        return CreateFile(Path.Combine(Tools, Server));
    }

    private static string CreateFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
