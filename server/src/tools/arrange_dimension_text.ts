import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerArrangeDimensionTextTool(server: McpServer) {
  server.tool(
    "arrange_dimension_text",
    "Move texts of horizontal and vertical dimension chains so they do not overlap each other, the dimension lines " +
      "or the crop edge. A text that is free stays; otherwise it slides along its line, goes to the other side or to " +
      "a second tier, whichever is free first. Reports kept, moved and unresolved texts per view; dryRun only counts.",
    {
      views: z.array(z.string()).min(1).describe("View names or ids."),
      gapMm: z.number().min(0).max(10).optional().describe("Paper gap between text and line. Defaults to 0.75."),
      clearanceMm: z.number().min(0).max(10).optional().describe("Paper clearance around each text. Defaults to 0.5."),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("arrange_dimension_text", args, 620000)
  );
}
