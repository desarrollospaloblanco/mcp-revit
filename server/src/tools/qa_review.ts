import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit } from "../utils/revitTool.js";

export function registerQaReviewTool(server: McpServer) {
  server.tool(
    "qa_review",
    "Model QA across open documents (all of them by default). orphan_tags: tags whose element is gone (room, area " +
      "and space tags without their room, element tags without a host), counted by category and view. warnings: the " +
      "model warnings grouped by text with element ids. fixOrphans: 'delete' removes orphan tags; 'reassign' re-hosts " +
      "element tags on the nearest element of the tagged category in the same view (within reassignWithinMeters), " +
      "keeping type and head position. Report first and ask the user before delete or reassign.",
    {
      documents: z.array(z.string()).optional().describe("Document titles; all open projects when omitted."),
      checks: z.array(z.enum(["orphan_tags", "warnings"])).optional().describe("Defaults to both."),
      fixOrphans: z.enum(["none", "delete", "reassign"]).optional().describe("Defaults to none (report only)."),
      reassignWithinMeters: z.number().min(0.05).max(20).optional().describe("Search radius for reassign. Defaults to 1.5."),
      limit: z.number().int().min(1).max(5000).optional().describe("Maximum rows per list. Defaults to 200."),
      swallowWarnings: z.boolean().optional().describe("Delete warnings raised while fixing. Defaults to true."),
    },
    async (args) => callRevit("qa_review", args, 920000)
  );
}
