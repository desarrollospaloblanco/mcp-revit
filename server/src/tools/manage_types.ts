import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";

export function registerManageTypesTool(server: McpServer) {
  server.tool(
    "manage_types",
    "Wall, floor, roof and ceiling types. list: types of a kind with width and layers. duplicate: copy baseType as " +
      "newName and give it layers listed exterior to interior (function, thickness in metres, material by name; " +
      "missing materials are created); the structure layers form the core. change_type: switch selected elements to " +
      "targetType (any category; family types as 'Family : Type').",
    {
      action: z.enum(["list", "duplicate", "change_type"]).optional().describe("Defaults to list."),
      kind: z.enum(["walls", "floors", "roofs", "ceilings"]).optional().describe("For list and duplicate. Defaults to walls."),
      baseType: z.string().optional(),
      newName: z.string().optional(),
      updateExisting: z.boolean().optional().describe("duplicate: rewrite the layers if newName exists."),
      layers: z
        .array(
          z.object({
            function: z.enum(["structure", "substrate", "insulation", "finish1", "finish2", "membrane"]).optional(),
            thicknessMeters: z.number().min(0),
            material: z.string().optional(),
          })
        )
        .optional(),
      createMissingMaterials: z.boolean().optional().describe("Defaults to true."),
      targetType: z.string().optional().describe("change_type: the new type name."),
      ...selection,
      ...writeOptions,
    },
    async (args) => callRevit("manage_types", args, 620000)
  );
}
