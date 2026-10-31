// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.SelfDiagnostics;
using OpenTelemetry.Trace;

namespace RoslynBridge.Telemetry;

/// <summary>
/// Traces, metrics and logs of tool calls, Roslyn requests and the workspace.
/// The bridge exports them over OTLP only when <see cref="EnableVariable"/> is set, and the OTEL_ variables configure the export.
/// </summary>
internal static class BridgeTelemetry
{
    internal const string Name = "RoslynBridge";
    internal const string EnableVariable = "ROSLYN_BRIDGE_TELEMETRY";
    internal const string ErrorType = "error.type";

    private const string DisableVariable = "OTEL_SDK_DISABLED";
    private const string VariablePrefix = "OTEL_";
    private const string ServiceName = "roslyn-bridge-mcp";
    private const string McpSdkMeter = "Experimental.ModelContextProtocol";
    private const string RpcSystem = "rpc.system";
    private const string RpcMethod = "rpc.method";
    private const string ReportReused = "roslyn_bridge.report.reused";

    internal static readonly ActivitySource Source = new(Name, Program.Version);

    private static readonly Meter _meter = new(Name, Program.Version);

    internal static readonly Histogram<double> ToolDuration = _meter.CreateHistogram<double>(
        "roslyn_bridge.tool.duration",
        unit: "s",
        description: "Duration of a tool call."
    );

    internal static readonly Histogram<int> ResultSize = _meter.CreateHistogram<int>(
        "roslyn_bridge.tool.result.size",
        unit: "{character}",
        description: "Characters of text that a tool call returns."
    );

    internal static readonly Histogram<double> QueueDuration = _meter.CreateHistogram<double>(
        "roslyn_bridge.tool.queue.duration",
        unit: "s",
        description: "Time a tool call waits for the calls before it."
    );

    internal static readonly Histogram<double> LoadDuration = _meter.CreateHistogram<double>(
        "roslyn_bridge.workspace.load.duration",
        unit: "s",
        description: "Duration of a Roslyn start, up to the end of the workspace load."
    );

    internal static readonly Histogram<double> SyncDuration = _meter.CreateHistogram<double>(
        "roslyn_bridge.workspace.sync.duration",
        unit: "s",
        description: "Time spent sending saved file changes to Roslyn before a request."
    );

    internal static readonly Histogram<int> EditOperations = _meter.CreateHistogram<int>(
        "roslyn_bridge.edit.operations",
        unit: "{operation}",
        description: "File writes, renames and deletions that an applied edit completes."
    );

    private static readonly Counter<long> _diagnosticReports = _meter.CreateCounter<long>(
        "roslyn_bridge.diagnostics.reports",
        unit: "{report}",
        description: "Diagnostic reports pulled from Roslyn, by whether the previous report still held."
    );

    private static readonly Histogram<double> _lspDuration = _meter.CreateHistogram<double>(
        "roslyn_bridge.lsp.duration",
        unit: "s",
        description: "Duration of a Roslyn request."
    );

    extension(HostApplicationBuilder builder)
    {
        public HostApplicationBuilder AddTelemetry()
        {
            if (!IsEnabled(Environment.GetEnvironmentVariables()))
            {
                return builder;
            }

            // The empty host reads no environment variable, and the exporter reads only configuration
            builder.Configuration.AddInMemoryCollection(
                Environment
                    .GetEnvironmentVariables()
                    .Cast<DictionaryEntry>()
                    .Select(static entry =>
                        KeyValuePair.Create((string)entry.Key, (string?)entry.Value)
                    )
                    .Where(static entry =>
                        entry.Key.StartsWith(VariablePrefix, StringComparison.Ordinal)
                    )
            );
            // Standard output carries the MCP messages, so no OTEL_ variable may route diagnostics to it
            builder.Services.Configure<SelfDiagnosticsOptions>(static options =>
                options.LogToStdout = false
            );
            builder
                .Services.AddOpenTelemetry()
                .ConfigureResource(static resource =>
                    resource
                        .Clear()
                        .AddService(ServiceName, serviceVersion: Program.Version)
                        .AddTelemetrySdk()
                        .AddEnvironmentVariableDetector()
                )
                .WithTracing(static tracing => tracing.AddSource(Name))
                .WithMetrics(static metrics => metrics.AddMeter(Name, McpSdkMeter))
                .WithLogging(
                    configureBuilder: null,
                    configureOptions: static logging => logging.IncludeFormattedMessage = true
                )
                .UseOtlpExporter();
            return builder;
        }
    }

    extension(IHost host)
    {
        /// <summary>
        /// Creates the providers before the host runs, so they observe the workspace load that starts first.
        /// </summary>
        public IHost StartTelemetry()
        {
            _ = host.Services.GetService<TracerProvider>();
            _ = host.Services.GetService<MeterProvider>();
            return host;
        }
    }

    internal static bool IsEnabled(IDictionary environment) =>
        IsTrue(environment[EnableVariable]) && !IsTrue(environment[DisableVariable]);

    public static void CountDiagnosticReport(bool reused) =>
        _diagnosticReports.Add(1, new KeyValuePair<string, object?>(ReportReused, reused));

    public static Task<T> TraceLspAsync<T>(string method, Func<Task<T>> request) =>
        TraceAsync(
            method,
            ActivityKind.Client,
            _lspDuration,
            new TagList { { RpcSystem, "jsonrpc" }, { RpcMethod, method } },
            request
        );

    public static Task TraceAsync(string name, Histogram<double> duration, Func<Task> operation) =>
        TraceAsync(
            name,
            ActivityKind.Internal,
            duration,
            tags: default,
            async () =>
            {
                await operation();
                return true;
            }
        );

    /// <summary>
    /// Records the duration with the tags, copies them to the activity and marks it failed when they hold an <see cref="ErrorType"/>.
    /// </summary>
    public static void Complete(
        Activity? activity,
        Histogram<double> duration,
        long started,
        TagList tags
    )
    {
        duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        if (activity is null)
        {
            return;
        }

        foreach (var (key, value) in tags)
        {
            activity.SetTag(key, value);
            if (string.Equals(key, ErrorType, StringComparison.Ordinal))
            {
                activity.SetStatus(ActivityStatusCode.Error);
            }
        }
    }

    public static string ErrorTypeOf(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    public static void MarkFailed(Activity? activity, Exception exception)
    {
        activity?.SetTag(ErrorType, ErrorTypeOf(exception));
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    private static async Task<T> TraceAsync<T>(
        string name,
        ActivityKind kind,
        Histogram<double> duration,
        TagList tags,
        Func<Task<T>> operation
    )
    {
        using var activity = Source.StartActivity(name, kind);
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await operation();
        }
        catch (Exception exception)
        {
            tags.Add(ErrorType, ErrorTypeOf(exception));
            throw;
        }
        finally
        {
            Complete(activity, duration, started, tags);
        }
    }

    private static bool IsTrue(object? value) =>
        value is string text
        && (
            string.Equals(text, "1", StringComparison.Ordinal)
            || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
        );
}
