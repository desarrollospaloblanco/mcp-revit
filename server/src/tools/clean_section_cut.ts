import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

export function registerCleanSectionCutTool(server: McpServer) {
  server.tool(
    "clean_section_cut",
    "Find and hide, in one section or elevation only, what its cut plane slices lengthwise: walls (or beams, any " +
      "straight location line) running parallel to the plane with the plane inside their thickness, which Revit fills " +
      "as solid black rectangles. The plane is the crop box face nearest the viewer. crossing adds elements of other " +
      "categories whose box the plane passes through, only inside a plan region (e.g. a pile or column at the edge " +
      "of the building); every cut column is solid, so a region is required. The view is looked up by name in each " +
      "document, so one call cleans the same section in several models. action list (default) reports id, category, " +
      "type, level, distance from the plane and extent; hide hides them with View.HideElements and records the ids on " +
      "the view; unhide restores only what this tool recorded, never what was hidden by hand. dryRun runs and rolls back.",
    {
      view: z.union([z.string(), z.number().int()]).describe("Section or elevation name (looked up in each document), or its element id."),
      documents: z.array(z.string()).optional().describe("Open document titles; defaults to documentTitle or the active one."),
      action: z.enum(["list", "hide", "unhide"]).optional().describe("Defaults to list."),
      categories: z
        .array(z.string())
        .optional()
        .describe("Categories tested as parallel to the plane. Defaults to ['OST_Walls']; add OST_StructuralFraming for beams."),
      crossing: z
        .object({
          categories: z.array(z.string()).min(1).describe("e.g. ['OST_StructuralColumns', 'OST_StructuralFoundation']."),
          region: z.object({ min: point2, max: point2 }).describe("Plan rectangle in metres; only elements whose box touches it."),
          level: z.string().optional().describe("Only elements based on this level."),
        })
        .optional()
        .describe("Elements of other categories crossed by the plane inside a region."),
      toleranceMeters: z.number().min(0).max(1).optional().describe("Slack around the thickness or box. Defaults to 0.03."),
      angleToleranceDegrees: z.number().min(0).max(45).optional().describe("How far from parallel a line may be. Defaults to 1."),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("clean_section_cut", args, 320000)
  );
}
