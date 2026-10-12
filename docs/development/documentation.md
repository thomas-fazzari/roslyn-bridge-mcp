# Documentation

Files under `docs` are the sources of the [VitePress](https://vitepress.dev/) site. The user pages explain how to install and use the bridge. The developer guide under `docs/development` explains how the bridge is built and how to change it.

## What to document

Document what stays true across releases: responsibilities, invariants and the reason behind them, contracts with clients and workflows for common changes. Leave tuned values, such as timeouts and limits, in the code, unless callers need to know what happens when a limit is exceeded.

Describe rules and responsibilities, not the current implementation. Do not describe individual tests or list files a reader can see in the tree. Do not document planned or unfinished work.

The guides are the source of truth. `AGENTS.md` holds short rules for agents and links to the guide pages instead of repeating them.

## Pages

Keep each page on one subject, and link to the page that owns an explanation instead of repeating it. Open each page with a paragraph that names the code it covers. Add a row to the [common changes](index.md#common-changes) table when a page gains a workflow.

The `llms` files under `docs/public` are plain-text references for agents. Keep them in sync with the tool contracts.

## Site

Add each page to the sidebar in `docs/.vitepress/config.mjs`. `mise run docs:dev` serves the site locally. `mise run docs:check` lints Markdown, checks formatting and builds the site. A broken relative link fails the build, but a page missing from the sidebar does not.
