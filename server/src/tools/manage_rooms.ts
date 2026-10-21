import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

export function registerManageRoomsTool(server: McpServer) {
  server.tool(
    "manage_rooms",
    "Room set-up and clean-up. separators: draw room separation lines (pairs of points in metres) in a plan view. " +
      "dedupe_separators: delete separation lines that repeat another or run along a wall on the level. " +
      "delete_bad_rooms: delete unplaced, not enclosed and redundant rooms (on a level, or everywhere). place_all: " +
      "place a room in every enclosed region of a level that has none. set_rooms: set name, number, department and " +
      "parameters of the room at each point. dryRun rolls back.",
    {
      operation: z.enum(["separators", "dedupe_separators", "delete_bad_rooms", "place_all", "set_rooms"]),
      level: z.string().optional(),
      view: z.string().optional().describe("separators: the plan view."),
      lines: z.array(z.array(point2).length(2)).optional().describe("separators: [[from, to], ...]."),
      toleranceMeters: z.number().optional().describe("dedupe_separators: defaults to 0.02."),
      rooms: z
        .array(
          z.object({
            point: point2,
            name: z.string().optional(),
            number: z.string().optional(),
            department: z.string().optional(),
            parameters: z.record(z.union([z.string(), z.number(), z.boolean()])).optional(),
          })
        )
        .optional(),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("manage_rooms", args, 920000)
  );
}
