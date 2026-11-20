// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using RoslynBridge.Common;
using RoslynBridge.Features.Edit.CodeActions;
using RoslynBridge.Features.Edit.Proposals;
using RoslynBridge.Features.Edit.Rename;
using RoslynBridge.Features.Edit.RenameFile;
using RoslynBridge.Results;

namespace RoslynBridge.Features.Edit;

internal static class EditServices
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddEdit() =>
            services
                .AddSingleton<IToolHandler<RenameRequest, ToolResult>, RenameHandler>()
                .AddSingleton<IToolHandler<RenameFileRequest, ToolResult>, RenameFileHandler>()
                .AddSingleton<IToolHandler<CodeActionsRequest, ToolResult>, CodeActionsHandler>()
                .AddSingleton<IToolHandler<ProposalRequest, ToolResult>, ProposalHandler>();
    }
}
