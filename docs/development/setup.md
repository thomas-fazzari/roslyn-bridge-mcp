# Setup

Prerequisites: Git, Bash and [mise](https://mise.jdx.dev). mise installs the .NET SDK, Bun, CSharpier and lefthook at the versions pinned in `mise.toml`. On Windows, use Git Bash. Run commands from the repository root.

```sh
mise install
mise run install
mise run hooks:install
```

The Git hooks format staged files on commit and run `mise run check` before a push.

## Local settings

Put local environment variables in the `[env]` table of `mise.local.toml`. mise loads it for every task and Git ignores it. Existing environment variables take precedence. MCP clients launch the bridge with their own environment, not this one.

## Builds

Builds use Debug by default. Run `CONFIGURATION=Release mise run build` for a release build. Stop any bridge process that uses the build output before rebuilding it.
