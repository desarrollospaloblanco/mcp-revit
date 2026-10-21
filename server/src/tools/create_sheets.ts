import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerCreateSheetsTool(server: McpServer) {
  server.tool(
    "create_sheets",
    "Create sheets (a sheet with the same number is reused) with a title block, sheet parameters (e.g. Sheet Sort) " +
      "and views or schedules. Each view goes to positionMm on the paper, or by default to the top-left corner of the " +
      "title block plus margins (DPB standard), the next ones in a row to its right. Views already on another sheet " +
      "are reported, not moved. Follow the DPB sheet naming rule for numbers and names.",
    {
      sheets: z
        .array(
          z.object({
            number: z.string(),
            name: z.string().optional(),
            titleBlock: z.string().optional().describe("'Family : Type', type or family name."),
            parameters: z.record(z.union([z.string(), z.number()])).optional().describe("Sheet parameters to set."),
            views: z
              .array(
                z.object({
                  view: z.string().describe("View or schedule name or id."),
                  positionMm: z.array(z.number()).length(2).optional().describe("Box centre (viewports) or top-left (schedules) in mm."),
                })
              )
              .optional(),
          })
        )
        .min(1),
      titleBlock: z.string().optional().describe("Default title block; the first one in the model when omitted."),
      marginLeftMm: z.number().optional().describe("From the title block's left edge. Defaults to 25."),
      marginTopMm: z.number().optional().describe("From the title block's top edge. Defaults to 10."),
      gapMm: z.number().optional().describe("Between views in a row. Defaults to 10."),
      ...writeOptions,
    },
    async (args) => callRevit("create_sheets", args, 620000)
  );
}
