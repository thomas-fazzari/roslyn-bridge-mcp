// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Tests.Unit;

public sealed class SetupTests
{
    private const string Launcher = "/home/me/.dotnet/tools/roslyn-bridge-mcp";
    private const string Root = "/opt/dotnet";

    [Fact]
    public void DnxRunRegistersDnxBesideTheDotnetHost()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows registers dnx from PATH.");

        var command = Setup.BridgeCommand($"{Root}/dotnet", Root, [Root]);

        command.Should().StartWith($"{Root}/dnx {Setup.PackageId}@").And.EndWith(" --yes");
        command.Should().NotContain("+");
    }

    [Fact]
    public void LauncherGetsDotnetRootWhenItsInstallIsNotAHostRoot()
    {
        Assert.SkipWhen(
            OperatingSystem.IsWindows(),
            "Windows launchers find .NET from the registry."
        );

        Setup
            .BridgeCommand(Launcher, Root, ["/usr/share/dotnet"])
            .Should()
            .Be($"/usr/bin/env DOTNET_ROOT={Root} {Launcher}");
        Setup.BridgeCommand(Launcher, Root, [$"{Root}/"]).Should().Be(Launcher);
    }

    [Theory]
    [InlineData(
        "claude",
        "claude mcp add --scope user roslyn -- \"/opt/my tools/roslyn-bridge-mcp\""
    )]
    [InlineData("codex", "codex mcp add roslyn -- \"/opt/my tools/roslyn-bridge-mcp\"")]
    [InlineData("copilot", "copilot mcp add roslyn -- \"/opt/my tools/roslyn-bridge-mcp\"")]
    [InlineData("pi", "pi mcp add roslyn -- \"/opt/my tools/roslyn-bridge-mcp\"")]
    [InlineData("agy", "agy mcp add roslyn \"/opt/my tools/roslyn-bridge-mcp\"")]
    public void RegistrationQuotesABridgePathWithSpaces(string executable, string expected)
    {
        var agent = Setup.Agents.Single(agent =>
            string.Equals(agent.Executable, executable, StringComparison.Ordinal)
        );

        agent
            .Registration(Setup.BridgeCommand("/opt/my tools/roslyn-bridge-mcp", Root, [Root]))
            .Should()
            .Be(expected);
    }
}
