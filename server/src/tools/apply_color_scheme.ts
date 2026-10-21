import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";

export function registerApplyColorSchemeTool(server: McpServer) {
  server.tool(
    "apply_color_scheme",
    "Present a room or area plan: apply a color fill scheme (an existing one by name, or a new one by parameter " +
      "such as Department, Name or a shared parameter), set entry colors, place one color legend (replacing the " +
      "previous one) and tag every room or area without a tag, head on its point and no leader. Area plans use areas " +
      "of the view's area scheme. Needs Revit 2022+ for the scheme and legend.",
    {
      view: z.string().describe("Plan view name or id."),
      scheme: z.string().optional().describe("Existing color scheme name (or the name for a new one with byParameter)."),
      byParameter: z.string().optional().describe("Create or retarget the scheme to this parameter."),
      title: z.string().optional().describe("Legend title for a new scheme."),
      colors: z
        .record(z.union([z.array(z.number()).length(3), z.string()]))
        .optional()
        .describe("Colors per value: {'APTO A-1': [240, 200, 200]} or '#F0C8C8'."),
      legend: z.boolean().optional().describe("Place the legend. Defaults to true."),
      legendPoint: point2.optional().describe("Legend position in model metres; defaults right of the crop."),
      tags: z.boolean().optional().describe("Tag untagged rooms or areas. Defaults to true."),
      removeUnusedEntries: z.boolean().optional().describe("Drop scheme entries no element uses. Defaults to true."),
      setOnTemplate: z.boolean().optional().describe("If the view template controls the scheme, set it there (affects all its views)."),
      ...writeOptions,
    },
    async (args) => callRevit("apply_color_scheme", args, 320000)
  );
}
