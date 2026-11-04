---
name: roslyn
description: Use the mcp__roslyn__* tools for any C# task, before grep or reading whole files. Covers navigation, diagnostics, renames and code fixes.
---

# Roslyn MCP

## Setup

- Run `tool_search` with query `roslyn` once per session to load the tools.
- Omit optional arguments you do not set. Never send `null`.
- On error `loading`, retry the same call. `lsp {"action":"status"}` shows progress.

## Navigation

- Prefer `symbol` to a position. On `ambiguous_symbol` or `symbol_not_found`, send one returned candidate back unchanged.
- Read a member with `source` and list a type with `members` instead of reading whole files.
- Check `impact` before changing a symbol. Related symbols come back as names, so chain `callers` for callers of callers.
- Batch independent lookups of known symbols with `bulk_navigate`.

## Diagnostics

- Check only the files the task touches, such as `src/RoslynBridge/Tools/{NavigateTool,SymbolsTool}.cs`. Avoid scanning the whole workspace, split by project, with `"summary":true`.
- After a timeout, narrow the scope. If one file still times out, report it and stop.
- A truncated, incomplete, failed or `ids`-filtered result does not prove the files are clean. An `incomplete:` line lists files outside any project.
- Diagnostics do not replace `mise run check`.

## Edits

- To fix a diagnostic, pass its code as `diagnostic` with `file` instead of a position.
- The apply result ends with the diagnostics of the changed files. Without its totals line, run `diagnostics` on them.
- On `stale_edit`, request a new preview. Never reuse an old ID. Proposals expire after five minutes and on reload.
- A failed multi-file apply can leave partial changes. Read the receipt before retrying.
- Run `lsp {"action":"reload"}` when results stay stale after external changes.

Full reference with result formats: <https://thomas-fazzari.github.io/roslyn-bridge-mcp/llms.txt>
