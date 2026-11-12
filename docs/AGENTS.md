# Roslyn Bridge docs guidelines

Pages under `docs` are the user guide. They are written for people who install the bridge and use it through their agent, not for agents or devs.

- Describe what the user installs, configures and asks for, and what the tools return.
- Never mention source files, types, internals, tests or planned work.
- When a limit matters to callers, say what happens past it.
- Keep `docs/public/llms.txt` in sync with the tool contracts.
- Add every new page to the sidebar in `docs/astro.config.mjs`. The build does not catch a missing entry.
- Put `<` and `{` in inline code or a code block, because MDX reads them as JSX.
- Expand uncommon abbreviations on first use.
