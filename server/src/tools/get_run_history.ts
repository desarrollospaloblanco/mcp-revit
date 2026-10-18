import fs from "fs";
import path from "path";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { runsDir } from "../utils/mcpPaths.js";

interface RunFile {
  id: string;
  time: string;
  title: string;
  activeDocument?: string;
  policy: string;
  dryRun: boolean;
  outcome: string;
  success: boolean;
  durationMs: number;
  errorMessage?: string;
  changes?: { document: string; added: number; modified: number; deleted: number }[];
}

function runFiles(): string[] {
  if (!fs.existsSync(runsDir)) return [];
  return fs
    .readdirSync(runsDir)
    .filter((d) => /^\d{8}$/.test(d))
    .flatMap((d) =>
      fs
        .readdirSync(path.join(runsDir, d))
        .filter((f) => f.endsWith(".json"))
        .map((f) => path.join(runsDir, d, f))
    )
    .sort()
    .reverse();
}

function readRun(file: string): RunFile | undefined {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8")) as RunFile;
  } catch {
    return undefined;
  }
}

// Reads the run records the plugin writes, straight from disk, so it works even while Revit is busy
// (for example to see whether a run that timed out actually finished).
export function registerGetRunHistoryTools(server: McpServer) {
  server.tool(
    "get_run_history",
    "List recent send_code_to_revit runs, newest first: id, time, title, outcome (applied, dry_run_rolled_back, " +
      "discarded_by_user, failed_rolled_back, no_changes, applied_ungrouped), duration and change counts per document. " +
      "Records are kept 14 days. Use get_run for the full script and result of one run.",
    {
      limit: z.number().int().min(1).max(200).optional().describe("How many runs to return. Defaults to 20."),
      onlyFailures: z.boolean().optional().describe("Return only failed runs."),
      titleContains: z.string().optional().describe("Return only runs whose title contains this text."),
    },
    async (args) => {
      const limit = args.limit ?? 20;
      const rows = [];
      for (const file of runFiles()) {
        const run = readRun(file);
        if (!run) continue;
        if (args.onlyFailures && run.success) continue;
        if (args.titleContains && !(run.title ?? "").toLowerCase().includes(args.titleContains.toLowerCase())) continue;
        rows.push({
          id: run.id,
          time: run.time,
          title: run.title,
          activeDocument: run.activeDocument,
          policy: run.policy,
          dryRun: run.dryRun,
          outcome: run.outcome,
          success: run.success,
          durationMs: run.durationMs,
          error: run.errorMessage || undefined,
          changes: (run.changes ?? []).map((c) => `${c.document}: +${c.added} ~${c.modified} -${c.deleted}`),
        });
        if (rows.length >= limit) break;
      }
      return { content: [{ type: "text", text: JSON.stringify({ folder: runsDir, runs: rows }, null, 2) }] };
    }
  );

  server.tool(
    "get_run",
    "Return one recorded send_code_to_revit run: script, parameters, policy, outcome, result, error and the full change summary.",
    {
      runId: z.string().describe("Run id from send_code_to_revit or get_run_history, for example 20261018-153012-447."),
    },
    async (args) => {
      const day = args.runId.slice(0, 8);
      const file = path.join(runsDir, day, `${args.runId}.json`);
      if (!fs.existsSync(file)) {
        return { content: [{ type: "text", text: `No run ${args.runId} in ${runsDir}` }] };
      }
      return { content: [{ type: "text", text: fs.readFileSync(file, "utf8") }] };
    }
  );
}
