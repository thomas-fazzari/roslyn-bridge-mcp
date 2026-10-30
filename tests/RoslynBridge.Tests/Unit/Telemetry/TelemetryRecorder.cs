// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.Metrics;
using RoslynBridge.Telemetry;

namespace RoslynBridge.Tests.Unit.Telemetry;

/// <summary>
/// Collects the activities and measurements of the bridge while the test runs.
/// </summary>
internal sealed class TelemetryRecorder : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ConcurrentQueue<Measurement> _measurements = new();
    private readonly ActivityListener _activityListener;
    private readonly MeterListener _meterListener = new();

    public TelemetryRecorder()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                string.Equals(source.Name, BridgeTelemetry.Name, StringComparison.Ordinal),
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = _activities.Enqueue,
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener.InstrumentPublished = static (instrument, listener) =>
        {
            if (
                string.Equals(instrument.Meter.Name, BridgeTelemetry.Name, StringComparison.Ordinal)
            )
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _meterListener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) => Record(instrument, value, tags)
        );
        _meterListener.SetMeasurementEventCallback<int>(
            (instrument, value, tags, _) => Record(instrument, value, tags)
        );
        _meterListener.Start();
    }

    public Activity Span(string name) =>
        _activities
            .Should()
            .ContainSingle(activity =>
                string.Equals(activity.DisplayName, name, StringComparison.Ordinal)
            )
            .Subject;

    public IEnumerable<Measurement> Measurements(Instrument instrument) =>
        _measurements.Where(measurement => measurement.Instrument == instrument);

    public void Dispose()
    {
        _activityListener.Dispose();
        _meterListener.Dispose();
    }

    private void Record(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags
    ) =>
        _measurements.Enqueue(
            new Measurement(
                instrument,
                value,
                new Dictionary<string, object?>(tags.ToArray(), StringComparer.Ordinal)
            )
        );

    internal sealed record Measurement(
        Instrument Instrument,
        double Value,
        IReadOnlyDictionary<string, object?> Tags
    );
}
