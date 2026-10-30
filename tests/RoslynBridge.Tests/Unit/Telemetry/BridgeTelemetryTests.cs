// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Tests.Unit.Telemetry;

public sealed class BridgeTelemetryTests
{
    private const string Endpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string Disabled = "OTEL_SDK_DISABLED";

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("1", null, true)]
    [InlineData("true", null, true)]
    [InlineData("0", null, false)]
    [InlineData("1", "true", false)]
    public void ExportsOnlyWhenTheBridgeVariableOptsIn(
        string? enable,
        string? disable,
        bool expected
    )
    {
        // An endpoint inherited from the MCP client must not be enough
        var environment = new Hashtable(StringComparer.Ordinal)
        {
            [Endpoint] = "http://localhost:4318",
            [BridgeTelemetry.EnableVariable] = enable,
            [Disabled] = disable,
        };

        BridgeTelemetry.IsEnabled(environment).Should().Be(expected);
    }
}
