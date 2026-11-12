# Contributing

[ARCHITECTURE.md](ARCHITECTURE.md) explains the design. [AGENTS.md](AGENTS.md) states the code and test rules.

## Setup

This project uses [mise](https://mise.jdx.dev) as its tool manager and task runner:

```sh
mise install
mise run install
mise run hooks:install
```

A dev container is available in `.devcontainer`, with the tools and the Roslyn language server that the integration tests need.

## Validation

| Change                                   | Command                     |
| ---------------------------------------- | --------------------------- |
| C#, build files or tooling               | `mise run check`            |
| Documentation or Markdown only           | `mise run docs:check`       |
| Roslyn session, tools or workspace edits | `mise run test-integration` |

Arguments after `--` go to the test runner:

```sh
mise run test -- --filter-class RoslynBridge.Tests.Unit.Symbols.SymbolNameTests
```

Integration tests start the installed `roslyn-language-server`. Set `ROSLYN_BRIDGE_TEST_SERVER` to its executable when it is not on `PATH`.

Run `mise lock` after you change a tool in `mise.toml` or the SDK in `global.json`, including on a Renovate branch.

To try a local package, run `mise run pack`, then install it from `artifacts/nupkg`:

```sh
dotnet tool install ThomasFazzari.RoslynBridge --tool-path DIRECTORY --add-source artifacts/nupkg --version 0.0.0-dev
```

[bench/README.md](bench/README.md) explains how to run the benchmarks and read their results.

## Branches and commits

Branch from `master`. Name the branch after the type of its main commit and a short subject, such as `fix/by-ref-reads`. The `ci-ok` check must pass before the merge. A pull request is squashed into one commit, and its title becomes the commit message.

Commits follow [Conventional Commits](https://www.conventionalcommits.org/) with a title only, within 50 characters, as in `fix: name the missing member`. Put the description in the pull request. The `commit-msg` hook checks each commit, but it cannot see the title of a pull request.

Pick the first type that applies:

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

A tool name, a parameter, a result line, an error code, a command-line option or a release asset name is a public contract. Changing one is a `feat!` or `fix!`.
