# Roslyn Bridge docs guidelines

Pages under `docs` are the MDX sources of the Starlight site. The user pages explain how to install and use the bridge. The developer guide under `docs/development` explains how it is built and how to change it.

## What to document

- Document responsibilities, invariants with their reason, contracts with clients and workflows for common changes.
- Never list behaviors, settings, tuned values, tests, files or planned work. Describe the current design only.
- When a limit matters to callers, say what happens past it.
- Keep `docs/public/llms.txt`, the plain-text reference for agents, in sync with the tool contracts.

## Pages

- Keep each page on one subject. Link to the page that owns an explanation instead of repeating it.
- Open each page with a paragraph that names the code it covers.
- Give each page a frontmatter `title` and no `description`. Start the body at `##`.
- Link to other pages by their relative `.mdx` path. The build fails on a missing page.
- Put `<` and `{` in inline code or a code block, because MDX reads them as JSX.
- Add every new page to the sidebar in `docs/astro.config.mjs`. The build does not catch a missing entry.
- Add a row to the common changes table in `docs/development/index.mdx` when a page gains a workflow.

## Prose

- No filler, repetition or long parentheses.
- Expand uncommon abbreviations on first use.
- Lead with the behavior, responsibility or decision the reader needs.
- Write standalone prose. Keep the reason for a decision without referring to conversations, agent activity or the sequence of edits.
