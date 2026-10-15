// Copyright (C) RoslynBridge contributors
// SPDX-License-Identifier: GPL-3.0-only

namespace RoslynBridge.Tools;

internal static class ToolDescriptions
{
    // Claude Code keeps 2,048 characters of server instructions
    // Codex advises a self-contained first 512
    public const string ServerInstructions =
        "Roslyn MCP: C# diagnostics, navigation and semantic edits from the official Roslyn language server, on saved files in the session's workspace. Prefer it over text search when symbol identity matters. diagnostics checks the files a task touches without a build, but does not replace builds or tests. diagnostics, navigate and symbols are read-only. edit returns a preview and a proposalId. Apply it with the same action, that proposalId and apply: true, and only when the user asked for the change.\n\nScope: request diagnostics for the files the task touches. Omitting file or passing a broad glob checks every C# file, so do that only for a requested workspace audit and split it by project. limit caps returned items, not analysis time. On loading, retry the same call. After a timeout, narrow the scope. If one file still times out, report it and stop retrying. A failed, incomplete or truncated result does not prove the workspace is clean.\n\nNavigation: prefer symbol over a position. Read a declaration with source and list a type with members instead of reading whole files. Before changing a symbol, check its impact. On ambiguous_symbol, send back one returned candidate unchanged. Related symbols come back as names you can send again, for example to follow callers of callers. Set context: true to get source lines without reading files. Positions are one-based, except in raw lsp requests.\n\nEdits: when the user already asked for the change, apply: true on the first call skips the preview, for a rename or for code_actions with an actionIndex. Otherwise, send the listing's proposalId and actionIndex to preview an action, then apply its new proposalId. To fix a diagnostic, pass its code as diagnostic with the file instead of a position. After applying, check diagnostics for the affected files. On stale_edit, request a fresh proposal.";

    public const string File = "Workspace-relative file path.";
    public const string Symbol =
        "C# symbol name, such as Namespace.Type.Member, Type.Member or Type.Method(int). Replaces line and character. Add file to pick between same-named declarations. A constructor needs its parameter list.";
    public const string Line = "One-based line number.";
    public const string Character = "One-based UTF-16 character in the line.";
    public const string Limit = "Maximum returned results, from 1 to 250. Truncation is explicit.";
}
