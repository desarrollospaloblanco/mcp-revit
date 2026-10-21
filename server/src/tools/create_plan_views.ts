import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { viewSettings } from "../utils/viewSettings.js";

export function registerCreatePlanViewsTool(server: McpServer) {
  server.tool(
    "create_plan_views",
    "Create plan views per level (or duplicate an existing view under new names) and set them up in one call: " +
      "template, scale, detail level, crop as a plan rectangle in metres or around the level's walls and floors, view " +
      "range offsets from the level, hidden categories and CAD, and grids trimmed to the crop in that view only " +
      "(direction kept, so bubbles stay on their side). An existing view with the same name is updated, not duplicated.",
    {
      levels: z.array(z.string()).optional().describe("Levels to create plans on."),
      viewType: z.enum(["floor", "ceiling", "structural"]).optional().describe("Defaults to floor."),
      names: z.array(z.string()).optional().describe("View names, one per level or per duplicate."),
      nameFormat: z.string().optional().describe("Name pattern when names are not given, e.g. '{level} - Amoblada'. Defaults to '{level}'."),
      duplicateOf: z.string().optional().describe("Existing plan to duplicate instead of creating from levels (needs names)."),
      duplicateOption: z.enum(["duplicate", "withDetailing", "dependent"]).optional().describe("Defaults to duplicate."),
      crop: z.object({ min: point2, max: point2 }).optional().describe("Crop rectangle in metres."),
      cropToModel: z.boolean().optional().describe("Crop around the level's walls and floors."),
      cropMarginMeters: z.number().min(0).max(100).optional().describe("Margin added to the crop. Defaults to 1."),
      cropVisible: z.boolean().optional().describe("Show the crop region. Defaults to false."),
      annotationCrop: z.boolean().optional().describe("Turn on the annotation crop."),
      viewRange: z
        .object({ top: z.number().optional(), cut: z.number().optional(), bottom: z.number().optional(), depth: z.number().optional() })
        .optional()
        .describe("View range offsets in metres from the view's level."),
      trimGrids: z.boolean().optional().describe("Trim grids to the crop in these views."),
      ...viewSettings,
      ...writeOptions,
    },
    async (args) => callRevit("create_plan_views", args, 620000)
  );
}
