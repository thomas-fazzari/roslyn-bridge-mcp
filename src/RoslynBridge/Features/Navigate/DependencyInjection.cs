// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using RoslynBridge.Common;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Navigate;

internal static class NavigateServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddNavigate() =>
            services.AddSingleton<IToolHandler<NavigateRequest, ToolResult>, NavigateHandler>();
    }
}
