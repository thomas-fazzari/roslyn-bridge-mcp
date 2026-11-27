---
name: git-workflow
description: Use before you create a branch, write a commit message, title a pull request or split a change into stacked pull requests. Covers branch names, gh stack and the Conventional Commits type to pick.
---

# Git workflow

## Branches

Branch from `master` by default.

Name the branch after the type of its main commit and a short subject. The `ci-ok` check must pass before the merge.

## Stacked pull requests

Split a change that has several reviewable steps into a stack with `gh stack`, such as a new tool, then its docs. Each branch targets the branch below it and follows the rules of this skill.

- `gh stack init` starts a stack on `master`.
- `gh stack add <branch>` adds a branch on top of the stack.
- `gh stack submit` pushes the branches and creates their pull requests.
- `gh stack rebase` rebases the stack after a change to a lower branch.
- `gh stack sync` syncs the local stack with the remote.
- `gh stack merge` merges the stack.

## Commits

Commits follow Conventional Commits with a title only, within 50 characters. Put the description in the PR.

Pick the first type that applies to the main change. Tests, docs or formatting that come with a change of code do not count:

| Order | Question                                                  | Type       |
| ----- | --------------------------------------------------------- | ---------- |
| 1     | Does it fix a bug?                                        | `fix`      |
| 2     | Does it change functionality or the output a client sees? | `feat`     |
| 3     | Does it add or change tests?                              | `test`     |
| 4     | Does it change code style or formatting?                  | `style`    |
| 5     | Does it change documentation?                             | `docs`     |
| 6     | Does it change the build or the deployment?               | `build`    |
| 7     | Does it change infrastructure, DevOps or backups?         | `ops`      |
| 8     | Is it a maintenance or other non-code task?               | `chore`    |
| 9     | Does it restructure code specifically for performance?    | `perf`     |
| 10    | Does it restructure code without changing behavior?       | `refactor` |

A change to a public contract (tool name, parameter, result line, error code, command-line option or release asset name) is a `feat!` or `fix!`.
