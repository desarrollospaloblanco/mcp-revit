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
      "hidden-line style; names 'Elevación Norte/Sur/Oriente/Poniente'. sections adds cuts on the same box: the cut plane lies " +
      "exactly on x = at or y = at and the view looks one way from it (when sections is given and sides is not, only the sections are made).",
    {
      sides: z.array(z.enum(["north", "south", "east", "west"])).optional().describe("Defaults to all four, or none when sections is given."),
      region: z.object({ min: point2, max: point2 }).optional().describe("Plan box in metres; defaults to the extent of all walls."),
      bottomMeters: z.number().optional().describe("Lowest elevation shown; defaults to the walls' extent."),
      topMeters: z.number().optional().describe("Highest elevation shown."),
      marginMeters: z.number().min(0).max(100).optional().describe("Margin around the box in the view. Defaults to 3."),
      offsetMeters: z.number().min(0).max(500).optional().describe("Distance from the box to the section line. Defaults to 10."),
      nameFormat: z.string().optional().describe("Defaults to 'Elevación {side}'."),
      sideNames: z.record(z.string()).optional().describe("Names for {side}, e.g. {north: 'Norte'}."),
      sections: z
        .array(
          z.object({
            name: z.string().describe("View name, e.g. 'Sección Transversal A-A'; a section with that name is replaced."),
            axis: z.enum(["x", "y"]).describe("'y' cuts on the plane y = at, 'x' on the plane x = at."),
            at: z.number().describe("Coordinate of the cut plane in metres."),
            look: z
              .enum(["north", "south", "east", "west"])
              .optional()
              .describe("Where the viewer looks from the plane: north or south for axis y, east or west for axis x. Defaults to north or east."),
            depthMeters: z.number().positive().optional().describe("How far the view sees past the plane; defaults to the far edge of the region plus the margin."),
          })
        )
        .optional()
        .describe("Sections cut at given coordinates, framed by the same region, heights, margin and view settings."),
      ...viewSettings,
      ...writeOptions,
    },
    async (args) => callRevit("create_elevation_views", args, 320000)
  );
}
