// SPDX-FileCopyrightText: 2026 Thomas Fazzari
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace RoslynBridge.Requests;

internal enum LspAction
{
    Diagnostics = 0,

    Definition = 1,

    TypeDefinition = 2,

    Implementation = 3,

    References = 4,

    Hover = 5,

    Symbols = 6,

    Rename = 7,

    RenameFile = 8,

    CodeActions = 9,

    Status = 10,

    Reload = 11,

    Capabilities = 12,

    Request = 13,

    Callers = 14,

    Callees = 15,

    Supertypes = 16,

    Subtypes = 17,

    Source = 18,

    Members = 19,

    Impact = 20,

    Bulk = 21,

    Reads = 22,

    Writes = 23,
}

internal static class LspActions
{
    extension(LspAction action)
    {
        public string Name => JsonNamingPolicy.SnakeCaseLower.ConvertName(action.ToString());

        /// <summary>
        /// Whether the action reads from the navigate tool.
        /// </summary>
        public bool IsNavigation =>
            action
                is LspAction.Definition
                    or LspAction.TypeDefinition
                    or LspAction.Implementation
                    or LspAction.References
                    or LspAction.Hover
                    or LspAction.Source
                    or LspAction.Members
                    or LspAction.Impact
            || action.IsHierarchy
            || action.IsAccess;

        /// <summary>
        /// Whether the action lists the references that read or write a symbol.
        /// </summary>
        public bool IsAccess => action is LspAction.Reads or LspAction.Writes;

        /// <summary>
        /// Whether the action expands a call or type hierarchy.
        /// </summary>
        public bool IsHierarchy =>
            action
                is LspAction.Callers
                    or LspAction.Callees
                    or LspAction.Supertypes
                    or LspAction.Subtypes;

        /// <summary>
        /// Whether the action proposes or applies a workspace change.
        /// </summary>
        public bool IsChange =>
            action is LspAction.Rename or LspAction.RenameFile or LspAction.CodeActions;
    }
}
