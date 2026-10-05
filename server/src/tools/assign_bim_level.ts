import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerAssignBimLevelTool(server: McpServer) {
  server.tool(
    "assign_bim_level",
    "Write the quantification level onto slabs and beams. A slab is poured at the elevation of the " +
      "level above the storey it roofs, so for take-off it belongs to the storey underneath: this " +
      "writes the level immediately below the one Revit assigns. Elements classified as substructure " +
      "get a fixed text instead. Optionally prefixes the values with a running number so a schedule " +
      "grouped by this parameter sorts from the lowest level up instead of alphabetically.",
    {
      documentTitle: z
        .string()
        .optional()
        .describe(
          "Exact title of the open document to act on. Omit to use the active document. Pass this " +
            "whenever more than one model is open."
        ),
      parameterName: z
        .string()
        .optional()
        .describe("Text parameter to write. Defaults to BIM_Level. Must already exist on the elements."),
      categories: z
        .array(z.string())
        .optional()
        .describe("Categories to process. Defaults to Floors and StructuralFraming."),
      foundationText: z
        .string()
        .optional()
        .describe("Value written on substructure elements. Defaults to CIMENTACION."),
      foundationCodePrefix: z
        .string()
        .optional()
        .describe(
          "Elements whose type Assembly Code starts with this prefix are treated as substructure. " +
            "Defaults to 'A'. Pass an empty string to disable the exception."
        ),
      mergeLevels: z
        .array(z.string())
        .optional()
        .describe(
          "Level names that are reference heights rather than storeys, such as a pavement datum. " +
            "They are taken out of the chain so they never become the 'level below' and shift a whole floor."
        ),
      numberPrefix: z
        .boolean()
        .optional()
        .describe(
          "Prefix each value with '01. ', '02. ' and so on, ordered from the lowest level up. " +
            "Re-running strips any previous prefix first, so it stays idempotent. Defaults to false."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("assign_bim_level", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `assign bim level failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
