import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { viewSettings } from "../utils/viewSettings.js";

export function registerCreate3dViewTool(server: McpServer) {
  server.tool(
    "create_3d_view",
    "Create or update a saved isometric 3D view seen from a compass direction, with a section box around a plan " +
      "rectangle and/or a level range (bottomLevel, topLevel with offsets), or around elementIds plus padding. Unlike " +
      "capture_view it stays in the model. For perspectives use set_perspective_view.",
    {
      name: z.string().describe("View name; reused when it exists."),
      direction: z
        .enum(["southwest", "southeast", "northwest", "northeast", "north", "south", "east", "west", "top"])
        .optional()
        .describe("Where the camera stands. Defaults to southwest."),
      region: z.object({ min: point2, max: point2 }).optional().describe("Plan rectangle of the section box in metres."),
      bottomLevel: z.string().optional(),
      bottomOffsetMeters: z.number().optional(),
      topLevel: z.string().optional(),
      topOffsetMeters: z.number().optional(),
      elementIds: z.array(z.number()).optional().describe("Frame the section box on these elements."),
      paddingMeters: z.number().min(0).max(100).optional().describe("Padding around the box. Defaults to 1."),
      ...viewSettings,
      ...writeOptions,
    },
    async (args) => callRevit("create_3d_view", args, 320000)
  );
}
