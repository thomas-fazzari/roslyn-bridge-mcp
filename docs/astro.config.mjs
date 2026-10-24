import { existsSync } from "node:fs";
import { dirname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { satteri } from "@astrojs/markdown-satteri";
import starlight from "@astrojs/starlight";
import { defineConfig } from "astro/config";
import starlightThemeBlack from "starlight-theme-black";

const base = "/roslyn-bridge-mcp/";
const docsDir = dirname(fileURLToPath(import.meta.url));

// Pages link to each other by relative .mdx paths, which also work in an editor.
// This turns them into site routes and fails the build on a missing target.
function relativePageLinks({ fileURL }) {
  if (!fileURL) {
    return null;
  }
  const sourcePath = fileURLToPath(fileURL);
  return {
    name: "relative-page-links",
    link(node, ctx) {
      ctx.setProperty(node, "url", toRoute(node.url, sourcePath));
    },
  };
}

function toRoute(url, sourcePath) {
  const match = /^([^:#?]+\.mdx)(#.*)?$/.exec(url);
  if (!match) {
    return url;
  }
  const [, target, hash = ""] = match;
  const targetPath = resolve(dirname(sourcePath), target);
  if (!existsSync(targetPath)) {
    throw new Error(`${relative(docsDir, sourcePath)} links to missing page ${target}`);
  }
  const route = relative(docsDir, targetPath)
    .replace(/\.mdx$/, "")
    .replace(/(^|\/)index$/, "");
  return `${base}${route}${route ? "/" : ""}${hash}`;
}

export default defineConfig({
  site: "https://thomas-fazzari.github.io",
  base,
  srcDir: ".",
  outDir: "dist",
  markdown: { processor: satteri({ mdastPlugins: [relativePageLinks] }) },
  integrations: [
    starlight({
      title: "Roslyn Bridge MCP",
      description: "C# diagnostics, navigation and refactoring for coding agents.",
      plugins: [starlightThemeBlack({ docs: { showMarkdownActions: false } })],
      customCss: [
        "@fontsource/onest/400.css",
        "@fontsource/onest/500.css",
        "@fontsource/onest/600.css",
        "./styles/custom.css",
      ],
      social: [
        {
          icon: "github",
          label: "GitHub",
          href: "https://github.com/thomas-fazzari/roslyn-bridge-mcp",
        },
      ],
      sidebar: [
        { label: "Overview", link: "/" },
        "getting-started",
        "settings",
        "usage",
        {
          label: "Development",
          items: [
            { label: "Developer guide", slug: "development" },
            "development/setup",
            "development/validation",
            "development/contributing",
            "development/releases",
            { label: "C# conventions", slug: "development/conventions/csharp" },
            { label: "Testing conventions", slug: "development/conventions/testing" },
            "development/documentation",
          ],
        },
      ],
    }),
  ],
});
