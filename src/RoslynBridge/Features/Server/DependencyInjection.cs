// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using RoslynBridge.Common;
using RoslynBridge.Features.Server.Capabilities;
using RoslynBridge.Features.Server.Raw;
using RoslynBridge.Features.Server.Reload;
using RoslynBridge.Features.Server.Status;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Server;

internal static class ServerServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddServer() =>
            services
                .AddSingleton<IToolHandler<StatusRequest, SessionStatus>, StatusHandler>()
                .AddSingleton<IToolHandler<ReloadRequest, SessionStatus>, ReloadHandler>()
                .AddSingleton<IToolHandler<CapabilitiesRequest, JsonResult>, CapabilitiesHandler>()
                .AddSingleton<IToolHandler<RawRequest, ToolResult>, RawRequestHandler>();
    }
}
