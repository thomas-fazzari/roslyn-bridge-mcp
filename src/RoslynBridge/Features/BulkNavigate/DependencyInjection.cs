// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using RoslynBridge.Common;
using RoslynBridge.Results;

namespace RoslynBridge.Features.BulkNavigate;

internal static class BulkNavigateServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddBulkNavigate() =>
            services.AddSingleton<
                IToolHandler<BulkNavigateRequest, ToolResult>,
                BulkNavigateHandler
            >();
    }
}
