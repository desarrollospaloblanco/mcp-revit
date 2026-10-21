import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { viewSettings } from "../utils/viewSettings.js";

export function registerCreateElevationViewsTool(server: McpServer) {
  server.tool(
    "create_elevation_views",
    "Create orthographic facade views (north, south, east, west) as sections around one plan box, so several " +
      "models get identical framing when given the same region and heights. A side is named after the facade it " +
      "shows: 'north' shows the face whose outside points north. Views with the same name are replaced. Defaults to " +
      "hidden-line style; names 'Elevación Norte/Sur/Oriente/Poniente'.",
    {
      sides: z.array(z.enum(["north", "south", "east", "west"])).optional().describe("Defaults to all four."),
      region: z.object({ min: point2, max: point2 }).optional().describe("Plan box in metres; defaults to the extent of all walls."),
      bottomMeters: z.number().optional().describe("Lowest elevation shown; defaults to the walls' extent."),
      topMeters: z.number().optional().describe("Highest elevation shown."),
      marginMeters: z.number().min(0).max(100).optional().describe("Margin around the box in the view. Defaults to 3."),
      offsetMeters: z.number().min(0).max(500).optional().describe("Distance from the box to the section line. Defaults to 10."),
      nameFormat: z.string().optional().describe("Defaults to 'Elevación {side}'."),
      sideNames: z.record(z.string()).optional().describe("Names for {side}, e.g. {north: 'Norte'}."),
      ...viewSettings,
      ...writeOptions,
    },
    async (args) => callRevit("create_elevation_views", args, 320000)
  );
}
