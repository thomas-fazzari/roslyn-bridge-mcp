# Roslyn Bridge MCP

[![CI](https://img.shields.io/github/actions/workflow/status/thomas-fazzari/roslyn-bridge-mcp/ci.yml?branch=master&label=CI)](https://github.com/thomas-fazzari/roslyn-bridge-mcp/actions/workflows/ci.yml)
[![Version](https://img.shields.io/github/v/release/thomas-fazzari/roslyn-bridge-mcp)](https://github.com/thomas-fazzari/roslyn-bridge-mcp/releases/latest)
[![NuGet](https://img.shields.io/nuget/v/ThomasFazzari.RoslynBridge?logo=nuget&logoColor=white)](https://www.nuget.org/packages/ThomasFazzari.RoslynBridge)
[![Documentation](https://img.shields.io/badge/docs-Starlight-BC52EE?logo=astro&logoColor=white)](https://thomas-fazzari.github.io/roslyn-bridge-mcp/)
[![License](https://img.shields.io/github/license/thomas-fazzari/roslyn-bridge-mcp)](LICENSE)

C# diagnostics, navigation and refactoring for coding agents, backed by the official Roslyn language server through the Model Context Protocol (MCP).

Find definitions, implementations and references. Preview and apply renames, fixes and refactorings.
The bridge reads saved files and works independently of your editor.

[Get started](https://thomas-fazzari.github.io/roslyn-bridge-mcp/getting-started/) · [Usage](https://thomas-fazzari.github.io/roslyn-bridge-mcp/usage/) · [Development](https://thomas-fazzari.github.io/roslyn-bridge-mcp/development/)

## Install

With the .NET 10 SDK, install the bridge as a .NET tool. `setup` installs the Roslyn language server and prints the command that registers the bridge in each agent it finds:

```sh
dotnet tool install --global ThomasFazzari.RoslynBridge
roslyn-bridge-mcp setup
```

See [Get started](https://thomas-fazzari.github.io/roslyn-bridge-mcp/getting-started/) for details.

## Agent instructions (recommended)

Add these lines to your project's `AGENTS.md`, `CLAUDE.md` or the equivalent file of your client, so the agent uses the tools instead of text search:

```markdown
## C# code navigation

Use the `roslyn` MCP tools for C# code before text search or reading whole files. If your client defers MCP tools, load them first.
Send independent lookups of known symbols in one `bulk_navigate` call.
Read a whole file only to edit it.
```

The snippet is all the agent needs. Do not describe the tools, their parameters or how the bridge works. The MCP server already sends each tool's description and its own usage instructions to the client when it connects. Repeating them in `AGENTS.md` costs context in every session and goes out of date when the tools change.

## License

Licensed under the GNU Affero General Public License, version 3 or later, with additional terms under section 7. See [LICENSE](LICENSE) and [NOTICE](NOTICE).

Third-party notices are in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), which the NuGet package also contains.

## Contributors

[![Contributors](https://contrib.rocks/image?repo=thomas-fazzari/roslyn-bridge-mcp)](https://github.com/thomas-fazzari/roslyn-bridge-mcp/graphs/contributors)
