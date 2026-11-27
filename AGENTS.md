# Dear Clanker

This repository contains **Roslyn Bridge MCP**, an MCP server that gives coding agents C# diagnostics, navigation and refactoring through the official Roslyn language server. It ships as a .NET tool that any MCP client can launch. Read `ARCHITECTURE.md`.

## Code guidelines

- Prefer early returns and small helpers to compound conditions and nested ternaries.
- No comments/docstrings by default -> add one only for what the code or the signature cannot show (a `//` comment must stay within 2 lines).
- Change only what the task needs, with no interim code. Report unrelated problems instead of fixing them.
- With a tool change, update `docs/usage.mdx` and `docs/public/llms.txt`.
- Ask before you:
  - change a public contract: a tool name, parameter, result line, error code, command-line option or release asset name
  - add a lock or hold one across a Roslyn request, a file walk or another wait
  - stage, commit, push, tag, switch branches or change the git state in any other way

### Tests

- Write few tests. Add one only when its failure would reveal broken behavior. Never test wiring.
- Test through a tool call, never through its internals. Assert error codes and result lines, never message wording.
- Wait for an effect the test can observe, never with sleeps or delays.

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
