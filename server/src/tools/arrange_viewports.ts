import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

export function registerArrangeViewportsTool(server: McpServer) {
  server.tool(
    "arrange_viewports",
    "Align viewports on sheets. topLeft: put each sheet's viewport in the top-left corner of the title block plus " +
      "margins. modelPoint: make a model point (e.g. a grid crossing, in metres) land on the same paper position on " +
      "every sheet, given as sheetPointMm or read from referenceSheet, so plans of different levels overlay exactly. " +
      "Viewport outlines include annotation outside the crop, so align after crops, grids and titles are final. " +
      "Legends are skipped; viewNameContains picks the viewport when a sheet has several.",
    {
      mode: z.enum(["topLeft", "modelPoint"]).optional().describe("Defaults to topLeft."),
      sheets: z.array(z.string()).optional().describe("Sheet numbers; all sheets when omitted."),
      viewNameContains: z.string().optional(),
      marginLeftMm: z.number().optional().describe("topLeft: defaults to 25."),
      marginTopMm: z.number().optional().describe("topLeft: defaults to 10."),
      modelPoint: point2.optional().describe("modelPoint: the model point in metres."),
      sheetPointMm: z.array(z.number()).length(2).optional().describe("modelPoint: where it must land on the paper."),
      referenceSheet: z.string().optional().describe("modelPoint: take the paper position from this sheet."),
      ...writeOptions,
    },
    async (args) => callRevit("arrange_viewports", args, 620000)
  );
}
