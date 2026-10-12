import { defineConfig } from "vitepress";

export default defineConfig({
  title: "RoslynBridge",
  description: "C# diagnostics, navigation and refactoring for coding agents.",
  base: "/roslyn-bridge-mcp/",
  lang: "en-US",
  themeConfig: {
    nav: [
      { text: "Get started", link: "/getting-started" },
      { text: "Development", link: "/development/" },
    ],
    sidebar: [
      { text: "Overview", link: "/" },
      { text: "Get started", link: "/getting-started" },
      { text: "Usage", link: "/usage" },
      {
        text: "Development",
        items: [
          { text: "Developer guide", link: "/development/" },
          { text: "Setup", link: "/development/setup" },
          { text: "Validation", link: "/development/validation" },
          { text: "Releases", link: "/development/releases" },
          { text: "C# conventions", link: "/development/conventions/csharp" },
          { text: "Testing conventions", link: "/development/conventions/testing" },
          { text: "Documentation", link: "/development/documentation" },
        ],
      },
    ],
    search: { provider: "local" },
    outline: [2, 3],
    socialLinks: [{ icon: "github", link: "https://github.com/thomas-fazzari/roslyn-bridge-mcp" }],
  },
});
