import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, documentOption } from "../utils/revitTool.js";

export function registerListSheetsTool(server: McpServer) {
  server.tool(
    "list_sheets",
    "Read-only inventory of sheets: number, name, title block, frame and viewports (view, scale, centre and outline " +
      "in mm), schedules, and any sheet parameters asked for.",
    {
      numberContains: z.string().optional().describe("Only sheets whose number contains this text."),
      parameters: z.array(z.string()).optional().describe("Sheet parameters to read, e.g. ['Sheet Sort']."),
      ...documentOption,
    },
    async (args) => callRevit("list_sheets", args, 320000)
  );
}
