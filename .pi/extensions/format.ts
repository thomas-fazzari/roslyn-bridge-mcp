import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { execFile, type ExecException } from "node:child_process";
import { readFile } from "node:fs/promises";
import { resolve } from "node:path";
import { promisify } from "node:util";

const run = promisify(execFile);
const FORMAT_TIMEOUT_MS = 15_000;
const FORMATTERS: [RegExp, string[]][] = [
  [/\.(?:cs|csx|csproj|props|targets|slnx)$/u, ["csharpier", "format"]],
  [
    /\.(?:json|jsonc|ya?ml|toml|m?js|ts)$/u,
    ["oxfmt", "--write", "--no-error-on-unmatched-pattern"],
  ],
  [/\.mdx?$/u, ["rumdl", "fmt", "--silent"]],
];

function formatCommand(path: string): string[] | undefined {
  const command = FORMATTERS.find(([pattern]) => pattern.test(path))?.[1];
  if (!command) {
    return undefined;
  }
  return [...command, path];
}

function failureNote(program: string, path: string, error: unknown): string | undefined {
  const { code, killed, stderr } = error as ExecException & { stderr: string };
  if (typeof code === "string") {
    return undefined;
  }
  if (killed) {
    return `${program} timed out on ${path}.`;
  }
  return `${program} failed on ${path}:\n${stderr.trim()}`;
}

export default function (pi: ExtensionAPI) {
  pi.on("tool_result", async (event, ctx) => {
    const path = event.input["path"];
    const isFileTool = event.toolName === "edit" || event.toolName === "write";
    if (event.isError || !isFileTool || typeof path !== "string") {
      return;
    }
    const command = formatCommand(path);
    if (!command) {
      return;
    }
    const file = resolve(ctx.cwd, path);
    const before = await readFile(file, "utf8");
    const [program, ...args] = command;
    let note: string | undefined;
    try {
      await run(program, args, { cwd: ctx.cwd, timeout: FORMAT_TIMEOUT_MS });
      if ((await readFile(file, "utf8")) !== before) {
        note = `${program} reformatted ${path}. Read it again before editing it.`;
      }
    } catch (error) {
      note = failureNote(program, path, error);
    }
    if (!note) {
      return;
    }
    return { content: [...event.content, { type: "text", text: note }] };
  });
}
