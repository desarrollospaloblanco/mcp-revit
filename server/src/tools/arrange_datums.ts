import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

const mode = z
  .enum(["arrange", "heads", "skip"])
  .optional()
  .describe("arrange: set the lines and the heads (default). heads: only show or hide heads. skip: leave untouched.");
const include = z.array(z.string()).optional().describe("Names to work on; defaults to all.");

export function registerArrangeDatumsTool(server: McpServer) {
  server.tool(
    "arrange_datums",
    "Lay out levels and grids in sections and elevations, in one transaction. Levels: a 2D line from leftMeters " +
      "(default 1) beyond the region's left edge to rightMeters (default 3) beyond its right edge, as seen in the " +
      "view, with the head on the right by default. Grids: a vertical 2D line from bottomMeters (default crop bottom " +
      "+ 0.20) to topMeters (default highest level in the view + 1.50), head at the top by default. mode 'heads' only " +
      "shows or hides heads (e.g. bubble 'none' hides them, 'right' brings them back). The region defaults to the " +
      "extent of all walls. crop.bottomMeters/topMeters set the crop; crop.fitHeads (default true) then widens the " +
      "crop so no datum end or head is cut, so there is no need to hide heads before exporting. extendModelExtents " +
      "(default false) first lengthens each level's 3D extent to the region plus extendMarginMeters (default 2) and " +
      "to the plane of every given view, so levels missing from a view appear; this changes them in every view and " +
      "levels it cannot reach are reported. Heights are model elevations in metres. Reports per view the levels and " +
      "grids adjusted, the ones skipped and why, and the final crop. dryRun reports and rolls back.",
    {
      views: z.array(z.union([z.string(), z.number()])).min(1).describe("Section, elevation or detail views, by name or id."),
      region: z
        .object({ min: point2, max: point2 })
        .optional()
        .describe("Plan box in metres that the level lines span; defaults to the extent of all walls."),
      extendModelExtents: z.boolean().optional().describe("Lengthen the levels' 3D extents first. Defaults to false."),
      extendMarginMeters: z.number().min(0).max(100).optional().describe("Margin around the region for the 3D extents. Defaults to 2."),
      levels: z
        .object({
          mode,
          include,
          leftMeters: z.number().min(0).max(200).optional().describe("Line start beyond the region's left edge. Defaults to 1."),
          rightMeters: z.number().min(0).max(200).optional().describe("Line end beyond the region's right edge. Defaults to 3."),
          bubble: z.enum(["right", "left", "both", "none"]).optional().describe("Head side as seen on the sheet. Defaults to right."),
        })
        .optional(),
      grids: z
        .object({
          mode,
          include,
          bottomMeters: z.number().optional().describe("Elevation of the line's bottom end. Defaults to crop bottom + 0.20."),
          topMeters: z.number().optional().describe("Elevation of the line's top end. Defaults to the highest level shown + 1.50."),
          bubble: z.enum(["top", "bottom", "both", "none"]).optional().describe("Defaults to top."),
        })
        .optional(),
      crop: z
        .object({
          bottomMeters: z.number().optional().describe("Elevation of the crop bottom; turns the crop on."),
          topMeters: z.number().optional().describe("Elevation of the crop top; turns the crop on."),
          fitHeads: z
            .boolean()
            .optional()
            .describe("Widen the crop so datum ends and heads fit; never moves an edge set above. Defaults to true."),
          headPaperMm: z.number().min(0).max(100).optional().describe("Room left for a head, on paper. Defaults to 25."),
        })
        .optional(),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("arrange_datums", args, 620000)
  );
}
