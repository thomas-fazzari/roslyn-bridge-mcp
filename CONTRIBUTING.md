# Contributing

Thanks for helping with Roslyn Bridge. The [developer guide](https://thomas-fazzari.github.io/roslyn-bridge-mcp/development/) explains the design and the checks. This page covers what a pull request needs.

1. Branch from `master`. Name the branch after the type of your change, such as `fix/by-ref-reads` or `feat/settings-file`.
2. Start each commit message with a type, as in `fix: name the missing member`. A commit has a title only, so put the description in the pull request. Add `!` after the type for a breaking change. [Branches and commits](https://thomas-fazzari.github.io/roslyn-bridge-mcp/development/contributing/) lists the types.
3. Run `mise run check` before you push. For changes to the Roslyn session, tools or workspace edits, also run `mise run test-integration`. [Validation](https://thomas-fazzari.github.io/roslyn-bridge-mcp/development/validation/) lists the checks for each kind of change.
4. Open a pull request. Write its title like a commit message, because the squash merge uses it. The `ci-ok` check must pass.
