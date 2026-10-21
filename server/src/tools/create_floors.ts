import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";

const loop = z.array(point2).min(3);

export function registerCreateFloorsTool(server: McpServer) {
  server.tool(
    "create_floors",
    "Create floors from polygons in metres (first loop is the outline, the rest are holes; holes need Revit 2022+), " +
      "with type, level, offset and structural flag. levels repeats every polygon on each of those levels (typical " +
      "slabs). fromRooms creates finish floors from room boundaries (finish face or wall centre), one per room. With " +
      "runMark and replacePrevious (default) a re-run first deletes the floors of the previous run with that mark.",
    {
      floors: z
        .array(
          z.object({
            loops: z.array(loop).min(1),
            type: z.string().optional().describe("Floor type name; the first one when omitted."),
            level: z.string().optional(),
            offsetMeters: z.number().optional(),
            structural: z.boolean().optional(),
          })
        )
        .optional(),
      levels: z.array(z.string()).optional().describe("Put every polygon on each of these levels."),
      fromRooms: z
        .object({
          ...selection,
          departmentContains: z.string().optional(),
          type: z.string().optional(),
          offsetMeters: z.number().optional(),
          boundary: z.enum(["finish", "center"]).optional().describe("Defaults to finish."),
        })
        .optional()
        .describe("Finish floors from rooms (selection defaults to all rooms; use filter.level)."),
      replacePrevious: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("create_floors", args, 920000)
  );
}
