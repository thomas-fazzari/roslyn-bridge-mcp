// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Tests.Unit;

public sealed class SetupTests
{
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
}
