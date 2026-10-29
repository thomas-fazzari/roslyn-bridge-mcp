import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { spawnSync } from "node:child_process";

const FILE_TOOLS = new Set(["edit", "write"]);
const CSHARPIER_FILE = /\.(?:cs|csx|csproj|props|targets|slnx)$/u;
const OXFMT_FILE = /\.(?:md|mdx|json|jsonc|ya?ml|toml|m?js|ts)$/u;

function formatCommand(path: string): string[] | undefined {
  if (CSHARPIER_FILE.test(path)) {
    return ["csharpier", "format", path];
  }
  if (OXFMT_FILE.test(path)) {
    return ["bunx", "--bun", "oxfmt", "--write", "--no-error-on-unmatched-pattern", path];
  }
  return undefined;
}

export default function (pi: ExtensionAPI) {
  pi.on("tool_result", (event, ctx) => {
    const path = event.input["path"];
    if (event.isError || !FILE_TOOLS.has(event.toolName) || typeof path !== "string") {
      return;
    }
    const command = formatCommand(path);
    if (command) {
      const [program, ...args] = command;
      spawnSync(program, args, { cwd: ctx.cwd });
    }
  });
}
