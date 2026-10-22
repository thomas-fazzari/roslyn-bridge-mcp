// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nerdbank.Streams;
using RoslynBridge.Tools;

namespace RoslynBridge.Tests.Unit.Tools;

/// <summary>
/// Serves the registered tools over an in-memory transport, so a test calls them as an MCP client does.
/// </summary>
internal sealed class ToolServer : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _stop;
    private readonly Task _running;

    private ToolServer(
        ServiceProvider services,
        CancellationTokenSource stop,
        Task running,
        McpClient client
    )
    {
        _services = services;
        _stop = stop;
        _running = running;
        Client = client;
    }

    public McpClient Client { get; }

    public static async Task<ToolServer> StartAsync(
        LspTool tool,
        CancellationToken cancellationToken
    )
    {
        var (clientSide, serverSide) = FullDuplexStream.CreatePair();

        var collection = new ServiceCollection();
        collection.AddSingleton(tool);
        collection
            .AddMcpServer()
            .WithStreamServerTransport(serverSide, serverSide)
            .WithRoslynTools();

        var services = collection.BuildServiceProvider();
        var stop = new CancellationTokenSource();
        var running = services.GetRequiredService<McpServer>().RunAsync(stop.Token);
        var client = await McpClient.CreateAsync(
            new StreamClientTransport(clientSide, clientSide),
            cancellationToken: cancellationToken
        );
        return new ToolServer(services, stop, running, client);
    }

    // The client leaves the server input open, so the server runs until it is stopped
    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        await _running;
        _stop.Dispose();
        await _services.DisposeAsync();
    }
}
