import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

export function registerCreateAreaPlanFromPolygonsTool(server: McpServer) {
  server.tool(
    "create_area_plan_from_polygons",
    "Area plan from given polygons instead of rooms (covered areas under slabs, lots, zones): polygon edges become " +
      "area boundary lines (shared edges drawn once), each polygon gets an area with name, number and parameters, " +
      "placed at a point inside it, and tags without leader. An existing view of the same name is rebuilt. Use " +
      "apply_color_scheme afterwards for colors and legend; export_plan_geometry gives floor outlines to start from.",
    {
      level: z.string(),
      areaScheme: z.string(),
      viewName: z.string().optional().describe("Defaults to '<level> - <areaScheme>'."),
      areas: z
        .array(
          z.object({
            loop: z.array(point2).min(3),
            holes: z.array(z.array(point2).min(3)).optional(),
            name: z.string().optional(),
            number: z.string().optional(),
            parameters: z.record(z.union([z.string(), z.number(), z.boolean()])).optional(),
          })
        )
        .min(1),
      tags: z.boolean().optional().describe("Defaults to true."),
      ...writeOptions,
    },
    async (args) => callRevit("create_area_plan_from_polygons", args, 920000)
  );
}
