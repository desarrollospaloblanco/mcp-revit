import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, documentOption, point2 } from "../utils/revitTool.js";

export function registerExportPlanGeometryTool(server: McpServer) {
  server.tool(
    "export_plan_geometry",
    "Read-only dump of a level's plan geometry in metres, for analysis outside Revit (Python, plotting, checks). " +
      "walls: axis, exterior and interior face lines, width, type, height, exterior normal. openings: doors and " +
      "windows with point, host, width, height, sill, facing and hand. floors: top face loops, type, offset, " +
      "thickness. rooms: number, name, department, area and boundary loops at wall centres. columns: boxes and " +
      "points. cad: imported CAD curves flattened to plan, grouped by layer. categories adds boxes of any other " +
      "BuiltInCategory (e.g. OST_Stairs, OST_Furniture). Written to a JSON file under %APPDATA%\\RevitMCP\\exports " +
      "(the answer has the path and counts); inline=true returns the JSON itself, only for small levels.",
    {
      level: z.string().optional().describe("Level name; every level when omitted."),
      include: z
        .array(z.enum(["walls", "openings", "floors", "rooms", "columns", "cad"]))
        .optional()
        .describe("Parts to export. Defaults to everything except cad."),
      categories: z.array(z.string()).optional().describe("Extra BuiltInCategory names exported as boxes."),
      region: z.object({ min: point2, max: point2 }).optional().describe("Only elements whose box touches this plan rectangle."),
      maxCadCurves: z.number().int().min(100).max(500000).optional().describe("Cap on CAD curves. Defaults to 20000."),
      inline: z.boolean().optional().describe("Return the JSON instead of writing a file."),
      outputPath: z.string().optional().describe("Where to write the JSON."),
      ...documentOption,
    },
    async (args) => callRevit("export_plan_geometry", args, 620000)
  );
}
