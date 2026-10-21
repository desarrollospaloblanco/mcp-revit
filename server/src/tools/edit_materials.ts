import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerEditMaterialsTool(server: McpServer) {
  server.tool(
    "edit_materials",
    "Materials by name. usage: where each material is used (category defaults, type layers, element counts by " +
      "category), e.g. to find why something renders in an unexpected color. create: new materials (with color). " +
      "color: set the shaded color and, by default, the render appearance color; an appearance shared with other " +
      "materials is duplicated first so they keep theirs; removeTexture drops a texture that hides the color.",
    {
      action: z.enum(["usage", "create", "color"]).optional().describe("Defaults to usage."),
      materials: z.array(z.string()).min(1),
      color: z.union([z.array(z.number()).length(3), z.string()]).optional().describe("[r, g, b] or '#RRGGBB'."),
      renderAppearance: z.boolean().optional().describe("Also change the render appearance. Defaults to true."),
      removeTexture: z.boolean().optional(),
      surfacePattern: z.boolean().optional().describe("Also color the surface pattern."),
      transparency: z.number().int().min(0).max(100).optional(),
      ...writeOptions,
    },
    async (args) => callRevit("edit_materials", args, 620000)
  );
}
