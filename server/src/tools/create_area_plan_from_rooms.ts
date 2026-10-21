import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { writeOptions } from "../utils/revitTool.js";

export function registerCreateAreaPlanFromRoomsTool(server: McpServer) {
  server.tool(
    "create_area_plan_from_rooms",
    "Build an area plan with one area per apartment from the rooms of a level. Rooms are grouped by a parameter " +
      "(Department by default); rooms of a value that do not touch (the same apartment type in two towers) become " +
      "separate areas numbered value.1, value.2 from west to east. Boundary lines follow each group's outline at wall " +
      "centres (interior walls of an apartment are dropped; columns and shafts inside stay included unless " +
      "includeHoles). Each area gets name (namePrefix + value, or commonName for common values), number, the type " +
      "parameter (typePrefix + text before typeSeparator) and comments; optionally a color scheme, tags without leader " +
      "and a legend. An existing view of the same name is rebuilt (areas and boundary lines replaced). Returns m2 per " +
      "area against the sum of its rooms, and problems such as areas not enclosed.",
    {
      roomsLevel: z.string().describe("Level the rooms are on."),
      areaLevel: z.string().optional().describe("Level of the area plan; defaults to roomsLevel."),
      areaScheme: z.string().optional().describe("Area scheme name. Defaults to Apartamentos."),
      viewName: z.string().optional().describe("Area plan name. Defaults to '<areaLevel> - Apartamentos'."),
      groupBy: z.string().optional().describe("Room parameter that names the apartment. Defaults to Department."),
      commonValues: z.array(z.string()).optional().describe("Values treated as common circulation. Defaults to COMÚN, CIRCULACIÓN."),
      commonName: z.string().optional().describe("Area name for common values. Defaults to CIRCULACIÓN."),
      namePrefix: z.string().optional().describe("Prefix of apartment area names. Defaults to 'APTO '."),
      typeParameter: z.string().optional().describe("Area parameter for the type. Defaults to 'Tipo de Apartamento'."),
      typePrefix: z.string().optional().describe("Defaults to 'TIPO '."),
      typeSeparator: z.string().optional().describe("The type is the value before this text. Defaults to '-'."),
      commonType: z.string().optional().describe("Type for common areas. Defaults to 'CIRCULACIÓN COMÚN'."),
      colorScheme: z.string().optional().describe("Color fill scheme of the area scheme to apply to the view."),
      tags: z.boolean().optional().describe("Tag the areas. Defaults to true."),
      legend: z.boolean().optional().describe("Place a color legend when a color scheme is set. Defaults to true."),
      includeHoles: z.boolean().optional().describe("Keep columns and shafts inside apartments as holes. Defaults to false."),
      hideImports: z.boolean().optional().describe("Hide imported CAD in the plan. Defaults to true."),
      replace: z.boolean().optional().describe("Rebuild an existing view of the same name. Defaults to true."),
      ...writeOptions,
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_area_plan_from_rooms", args, 620000);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `create_area_plan_from_rooms failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
