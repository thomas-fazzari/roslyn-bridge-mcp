// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Tests.Unit.Telemetry;

/// <summary>
/// Runs alone, because its listeners would also record the activities and measurements of other tests.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryCollection
{
    public const string Name = "Telemetry";
}
