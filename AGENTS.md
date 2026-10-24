# Dear Clanker

This repository contains **Roslyn Bridge MCP**, an MCP server that gives coding agents C# diagnostics, navigation and refactoring through the official Roslyn language server.
It ships as a native ahead-of-time executable that any MCP client can launch.

## Code navigation

Use the `roslyn` MCP tools for C# code before text search or reading whole files. When they are deferred, load them with ToolSearch first.

- Find a symbol with `symbols`, and read its declaration with `navigate` and `source`.
- List a type with `members`. Find usages with `references`, `callers` or `impact`.
- Send independent lookups of known symbols in one `bulk_navigate` call.
- Read a whole file only to edit it.

## Validation

Before reporting work as done, run the checks that [validation](docs/development/validation.mdx) lists for the areas you changed.

## Commits

Follow [branches and commits](docs/development/contributing.mdx).

- Start the title with a type, as in `fix: name the missing member`. Add `!` for a breaking change.
- Keep the title within 50 characters. Write a title only. No description and no trailer, such as `Co-Authored-By`. The description goes in the pull request.
- Name a branch `type/subject`. Never commit, push or tag without being asked.

## Code

Follow the [C# conventions](docs/development/conventions/csharp.mdx) and the [testing conventions](docs/development/conventions/testing.mdx).

- Never relax an analyzer rule to make a change pass. Ask first.
- Prefer plain control flow: early returns, small helpers, `if` and `switch` over nested ternaries.
- Write almost no comments. Add one only for a constraint the code cannot show, in one line.
- Do not write interim code that a later step will delete.
- Before writing a type or helper, search for an existing one. After each change, reread the diff for duplication and leftovers.
- Change only what the task needs. Report unrelated problems instead of fixing them.
- Treat tool names, parameters, result lines, error codes, command-line options and release asset names as a public contract. Ask before changing one.
- Ask before adding a lock or holding one across a Roslyn request, a file walk or another wait.

## Reviews

- List every simplification you considered, applied or rejected, with the reason for each.
- Check whether a built-in primitive could replace each task, channel or lock.

## Prose

Follow [documentation](docs/development/documentation.mdx) when writing pages under `docs`.

- Use short sentences and common words.
- Expand uncommon abbreviations on first use.
- Avoid semicolons, em dashes, filler and repeated facts.
- Write standalone prose. Keep the reason for a decision without referring to conversations, agent activity or the sequence of edits.

## Resources

The [developer guide](docs/development/index.mdx) states the design rules, and its common changes table names the page to read for each kind of change.
