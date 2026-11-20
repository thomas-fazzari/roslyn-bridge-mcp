// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace RoslynBridge.Common;

/// <summary>
/// An MCP tool. Its single public method handles the call.
/// </summary>
internal interface IRoslynTool
{
    static abstract string Name { get; }

    static abstract string Description { get; }

    /// <summary>
    /// A tool that is not read-only may write workspace files.
    /// </summary>
    static abstract bool ReadOnly { get; }

    static McpServerTool Create<TTool>()
        where TTool : class, IRoslynTool =>
        McpServerTool.Create(
            typeof(TTool)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Single(method => method.ReturnType == typeof(Task<CallToolResult>)),
            static context => ActivatorUtilities.CreateInstance<TTool>(context.Services!),
            new McpServerToolCreateOptions
            {
                Name = TTool.Name,
                Description = TTool.Description,
                ReadOnly = TTool.ReadOnly,
                Destructive = !TTool.ReadOnly,
                Idempotent = TTool.ReadOnly,
                OpenWorld = false,
            }
        );
}
