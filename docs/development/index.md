---
description: "Introduces the developer guide, its design rules and the page to read for each common change."
tags: [kind/index, area/project]
---

# Developer guide

RoslynBridge is a native executable that serves C# tools over the Model Context Protocol (MCP). It starts the official Roslyn language server, keeps it in sync with the workspace files and turns tool calls into Language Server Protocol (LSP) requests.

## Design rules

- Keep the bridge standalone and usable from any MCP client.
- Use the official [MCP SDK](https://csharp.sdk.modelcontextprotocol.io/) for the tools and [StreamJsonRpc](https://microsoft.github.io/vs-streamjsonrpc/) for Roslyn.
- Reserve standard output for MCP messages. Send logs to standard error.
- Before applying Roslyn changes, check that each file still matches the version used to compute them.
- `.gitignore` is an allowlist. Allow each new project path explicitly.

## Sections

- [Setup](setup.md) installs the tools and explains local settings.
- [Validation](validation.md) lists the checks to run and how to run a subset of tests.
- [Releases](releases.md) covers native builds and publishing.
- [C# conventions](conventions/csharp.md) and [testing conventions](conventions/testing.md) state the rules for code and tests.
- [Documentation](documentation.md) defines what these pages cover and how to write them.

## Layout

| Path                        | Contents                                               |
| --------------------------- | ------------------------------------------------------ |
| `src/RoslynBridge/Tools`    | MCP tools and the dispatcher they share                |
| `src/RoslynBridge/Requests` | Tool request model                                     |
| `src/RoslynBridge/Queries`  | Read-only Roslyn queries and their result shapes       |
| `src/RoslynBridge/Changes`  | Rename and code action proposals and their application |
| `src/RoslynBridge/Output`   | Text rendering of tool results                         |
| `src/RoslynBridge/Lsp`      | Roslyn process, protocol and document synchronization  |
| `src/RoslynBridge/Symbols`  | Symbol name resolution                                 |
| `src/RoslynBridge/Editing`  | Workspace file edits and their previews                |
| `tests/RoslynBridge.Tests`  | Unit tests, integration tests and fixtures             |
| `eng`                       | Analyzer rules and CI tasks                            |
| `docs`                      | VitePress pages and configuration                      |

## Common changes

| Change                             | Start with                                    |
| ---------------------------------- | --------------------------------------------- |
| Change an analyzer or style rule   | [Analyzers](conventions/csharp.md#analyzers)  |
| Report a new kind of failure       | [Errors](conventions/csharp.md#errors)        |
| Add or change a test               | [Testing conventions](conventions/testing.md) |
| Publish a release                  | [Releases](releases.md)                       |
| Add or change a documentation page | [Documentation](documentation.md)             |
