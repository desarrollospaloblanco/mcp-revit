import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerRunMarksTool(server: McpServer) {
  server.tool(
    "run_marks",
    "Work with the run marks that tools store (hidden, not in Comments) on what they create when given runMark. " +
      "count: marks in the document with element counts per category. list: elements of a mark. select: select them " +
      "in the active document. delete: delete everything a mark created, e.g. to redo a run cleanly.",
    {
      action: z.enum(["count", "list", "select", "delete"]).optional().describe("Defaults to count."),
      mark: z.string().optional().describe("The run mark; required for select and delete."),
      limit: z.number().int().min(1).max(5000).optional().describe("Maximum elements listed. Defaults to 500."),
      ...writeOptions,
    },
    async (args) => callRevit("run_marks", args, 320000)
  );
}
