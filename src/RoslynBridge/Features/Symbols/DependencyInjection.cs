// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using RoslynBridge.Common;

namespace RoslynBridge.Features.Symbols;

internal static class SymbolsServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSymbols() =>
            services
                .AddSingleton<IToolHandler<SymbolsRequest, SymbolList>, SymbolsHandler>()
                .AddSingleton<IToolHandler<UnusedRequest, UnusedSymbols>, UnusedHandler>();
    }
}
