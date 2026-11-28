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
/// Exports over OTLP only when <see cref="ENABLE_VARIABLE"/> is set. The OTEL_ variables configure the export.
/// </summary>
internal static class BridgeTelemetry
{
    internal const string NAME = "RoslynBridge";
    internal const string ENABLE_VARIABLE = "ROSLYN_BRIDGE_TELEMETRY";
    internal const string ERROR_TYPE = "error.type";

    private const string DISABLE_VARIABLE = "OTEL_SDK_DISABLED";
    private const string VARIABLE_PREFIX = "OTEL_";
    private const string SERVICE_NAME = "roslyn-bridge-mcp";
    private const string MCP_SDK_METER = "Experimental.ModelContextProtocol";
    private const string RPC_SYSTEM = "rpc.system";
    private const string RPC_METHOD = "rpc.method";
    private const string REPORT_REUSED = "roslyn_bridge.report.reused";

    internal static readonly ActivitySource Source = new(NAME, BridgeVersion.Current);

    private static readonly Meter _meter = new(NAME, BridgeVersion.Current);

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
                return builder;

            // The empty host reads no environment variable, and the exporter reads only configuration
            builder.Configuration.AddInMemoryCollection(
                Environment
                    .GetEnvironmentVariables()
                    .Cast<DictionaryEntry>()
                    .Select(static entry =>
                        KeyValuePair.Create((string)entry.Key, (string?)entry.Value)
                    )
                    .Where(static entry =>
                        entry.Key.StartsWith(VARIABLE_PREFIX, StringComparison.Ordinal)
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
                        .AddService(SERVICE_NAME, serviceVersion: BridgeVersion.Current)
                        .AddTelemetrySdk()
                        .AddEnvironmentVariableDetector()
                )
                .WithTracing(static tracing => tracing.AddSource(NAME))
                .WithMetrics(static metrics => metrics.AddMeter(NAME, MCP_SDK_METER))
                .WithLogging(configureBuilder: null, configureOptions: static logging =>
                    logging.IncludeFormattedMessage = true
                )
                .UseOtlpExporter();
            return builder;
        }
    }

    extension(IHost host)
    {
        /// <summary>
        /// Creates the providers early, so they observe the workspace load that starts first.
        /// </summary>
        public IHost StartTelemetry()
        {
            _ = host.Services.GetService<TracerProvider>();
            _ = host.Services.GetService<MeterProvider>();
            return host;
        }
    }

    internal static bool IsEnabled(IDictionary environment) =>
        IsTrue(environment[ENABLE_VARIABLE]) && !IsTrue(environment[DISABLE_VARIABLE]);

    public static void CountDiagnosticReport(bool reused) =>
        _diagnosticReports.Add(1, new KeyValuePair<string, object?>(REPORT_REUSED, reused));

    public static Task<Result<T>> TraceLspAsync<T>(string method, Func<Task<Result<T>>> request) =>
        TraceAsync(
            method,
            ActivityKind.Client,
            _lspDuration,
            new TagList { { RPC_SYSTEM, "jsonrpc" }, { RPC_METHOD, method } },
            request,
            static result => result is ToolError error ? error : null
        );

    public static Task<Result> TraceAsync(
        string name,
        Histogram<double> duration,
        Func<Task<Result>> operation
    ) =>
        TraceAsync(name, ActivityKind.Internal, duration, tags: default, operation, static result =>
            result is ToolError error ? error : null
        );

    /// <summary>
    /// Marks the activity failed when the tags contain an <see cref="ERROR_TYPE"/>.
    /// </summary>
    public static void Complete(
        Activity? activity,
        Histogram<double> duration,
        long started,
        TagList tags
    )
    {
        duration.Record(TimeProvider.System.GetElapsedTime(started).TotalSeconds, tags);
        if (activity is null)
            return;

        foreach (var (key, value) in tags)
        {
            activity.SetTag(key, value);
            if (string.Equals(key, ERROR_TYPE, StringComparison.Ordinal))
                activity.SetStatus(ActivityStatusCode.Error);
        }
    }

    public static string ErrorTypeOf(Exception exception) =>
        exception.GetType().FullName ?? exception.GetType().Name;

    public static void MarkFailed(Activity? activity, string errorType)
    {
        activity?.SetTag(ERROR_TYPE, errorType);
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    private static async Task<T> TraceAsync<T>(
        string name,
        ActivityKind kind,
        Histogram<double> duration,
        TagList tags,
        Func<Task<T>> operation,
        Func<T, ToolError?> errorOf
    )
    {
        using var activity = Source.StartActivity(name, kind);
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            var result = await operation();
            if (errorOf(result) is { } error)
                tags.Add(ERROR_TYPE, error.Code);

            return result;
        }
        catch (Exception exception)
        {
            tags.Add(ERROR_TYPE, ErrorTypeOf(exception));
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
