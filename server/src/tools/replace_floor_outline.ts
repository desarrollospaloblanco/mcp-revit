import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

export function registerReplaceFloorOutlineTool(server: McpServer) {
  server.tool(
    "replace_floor_outline",
    "Replace the outer boundary of an existing floor's sketch with a new polygon in metres, keeping the floor (id, " +
      "type, parameters, hosted elements, slope arrow) and its holes unless new holes are given. Needs Revit 2022+. " +
      "Returns the area before and after.",
    {
      floorId: z.number(),
      outline: z.array(point2).min(3),
      holes: z.array(z.array(point2).min(3)).optional().describe("New holes; replaces the existing ones."),
      keepHoles: z.boolean().optional().describe("Defaults to true unless holes are given."),
      ...writeOptions,
    },
    async (args) => callRevit("replace_floor_outline", args, 320000)
  );
}
