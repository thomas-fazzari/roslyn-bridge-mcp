# Dear Clanker

This repository contains **Roslyn Bridge MCP**, an MCP server that gives coding agents C# diagnostics, navigation and refactoring through the official Roslyn language server.
It ships as a .NET tool that any MCP client can launch.

Read `ARCHITECTURE.md`.

## Code navigation

Use the `roslyn` MCP tools for C# code before text search or reading whole files. When they are deferred, load them with ToolSearch first. Read a whole file only to edit it.

## Code guidelines

- Prefer early returns and small helpers to compound conditions and nested ternaries.
- Comment only a constraint the code cannot show, in 2 lines at most.
- Add docstrings only when they tell the caller something the signature does not.
- Put private methods at the end of the type, before the `[LoggerMessage]` methods.
- Never write interim code that a later step will delete.
- Change only what the task needs. Report unrelated problems instead of fixing them.
- Treat tool names, parameters, result lines, error codes, command-line options and release asset names as a public contract. Ask before changing one.
- With a tool change, update `docs/usage.mdx` and `docs/public/llms.txt`.
- Ask before adding a lock or holding one across a Roslyn request, a file walk or another wait.

### Tests

- Add a test only when its failure would reveal broken behavior. Never add tests that only prove wiring.
- Test through a tool call, never through its internals. Assert error codes and result lines, never message wording.
- Replace a dependency with a small fake in `Fakes`. No mocking library.
- Wait for an effect the test can observe, never with sleeps or delays.

## Validation

Before reporting work as done, run `mise run check`. Also run `mise run test-integration` after changing the Roslyn session, tools or workspace edits. For a Markdown change only, `mise run docs:check` is enough.

## Commits

Follow the branch and commit rules in `CONTRIBUTING.md`. Never commit, push or tag without being asked.

## Prose

In comments, docs and pull requests:

- Use short sentences and common words. No semicolons or em dashes.
- Cut a sentence that says nothing instead of rewording it.
- State the specific number, case, command or behavior, never a vague claim.
- No filler ("note that", "in summary"), inflated words ("robust", "seamless", "leverage") or empty adverbs ("simply", "just").
- State the fact, not "not X, but Y".
- Use the present tense and one term per concept.
- Never narrate history or refer to the task or the conversation.
- No emoji or decorative characters.
