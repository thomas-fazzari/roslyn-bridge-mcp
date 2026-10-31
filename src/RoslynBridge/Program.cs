// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RoslynBridge.Lsp;
using RoslynBridge.Settings;
using RoslynBridge.Telemetry;

namespace RoslynBridge;

internal static class Program
{
    private const string Usage =
        "roslyn-bridge-mcp [--workspace DIRECTORY] [--server EXECUTABLE] [--dotnet-root DIRECTORY] [--startup-timeout SECONDS] [--request-timeout SECONDS] [--exclude FOLDER]...\nroslyn-bridge-mcp setup";

    // The SDK appends "+commit" to the informational version
    internal static readonly string Version = typeof(Program)
        .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];

    public static async Task Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            await Console.Error.WriteLineAsync(Usage);
            return;
        }

        if (args is [Setup.Command])
        {
            Environment.ExitCode = Setup.Run(Console.Out);
            return;
        }

        LaunchArguments arguments;
        try
        {
            arguments = LaunchArguments.Parse(args);
        }
        catch (InvalidLaunchArgumentsException exception)
        {
            await FailAsync($"{exception.Message}\n{Usage}");
            return;
        }

        if (!Directory.Exists(arguments.WorkspaceRoot))
        {
            await FailAsync($"The workspace directory does not exist: {arguments.WorkspaceRoot}");
            return;
        }

        SettingsMonitor settings;
        try
        {
            settings = new SettingsMonitor(() =>
                BridgeOptions.From(arguments, ProjectSettingsReader.Read(arguments.WorkspaceRoot))
            );
        }
        catch (InvalidSettingsException exception)
        {
            await FailAsync(exception.Message);
            return;
        }

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.AddStandardErrorConsole();
        builder.AddTelemetry();
        builder.Services.AddRoslynBridge(settings).AddRoslynMcpServer().WithStdioServerTransport();

        var host = builder.Build().StartTelemetry();
        // stdio transport delivers no tool call before RunAsync, so this cannot race one
        if (host.Services.GetRequiredService<WorkspacePaths>().ContainsProject())
        {
            await host.Services.GetRequiredService<RoslynSession>().StartLoadingAsync();
        }

        await host.RunAsync(CancellationToken.None);
    }

    private static async Task FailAsync(string message)
    {
        await Console.Error.WriteLineAsync(message);
        Environment.ExitCode = 1;
    }
}
