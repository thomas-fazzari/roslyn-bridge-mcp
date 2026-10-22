// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Tests.Unit;

public sealed class SetupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("roslyn-setup-").FullName;

    [Fact]
    public void ToolInstallRegistersTheToolLinkInsteadOfTheVersionedStoreFolder()
    {
        var link = Path.Combine(_root, Setup.ToolCommandName);
        File.WriteAllText(link, string.Empty);
        var executable = Path.Combine(_root, ".store", "tool", "1.0.0", "tools", "RoslynBridge");

        Setup.StableBridgePath(executable).Should().Be(link);
    }

    [Fact]
    public void BridgePathStaysTheProcessPathWithoutAToolLink()
    {
        var installed = Path.Combine(_root, "bin", Setup.ToolCommandName);
        var store = Path.Combine(_root, ".store", "tool", "1.0.0", "tools", "RoslynBridge");

        Setup.StableBridgePath(installed).Should().Be(installed);
        Setup.StableBridgePath(store).Should().Be(store);
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

        agent.Registration("/opt/my tools/roslyn-bridge-mcp").Should().Be(expected);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
