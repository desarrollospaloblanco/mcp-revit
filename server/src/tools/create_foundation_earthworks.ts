import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateFoundationEarthworksTool(server: McpServer) {
  server.tool(
    "create_foundation_earthworks",
    "Build excavation and backfill masses for foundations as Toposolids (Revit 2024 or newer). " +
      "For each footing or foundation beam the excavation runs from the underside of the element " +
      "up to the slab above it, and the backfill is that same mass with the concrete and any " +
      "columns cut out of it. Elements whose ceiling lands on their own top are shallow — the " +
      "excavation equals their volume and no fill mass is created for them. Types are named after " +
      "their thickness so they group on their own in a schedule, and each mass carries a Comments " +
      "value naming the element it came from.",
    {
      documentTitle: z
        .string()
        .optional()
        .describe(
          "Exact title of the open document to act on. Omit to use the active document. Pass this " +
            "whenever more than one model is open, because the active document can change mid-call."
        ),
      categories: z
        .array(z.string())
        .optional()
        .describe(
          "Categories to treat as foundations. Defaults to StructuralFoundation and StructuralFraming."
        ),
      framingCodePrefix: z
        .string()
        .optional()
        .describe(
          "Only framing whose type Assembly Code starts with this prefix is treated as a foundation " +
            "beam. Defaults to 'A' (substructure in Uniformat). Pass an empty string to include all framing."
        ),
      excavationCode: z
        .string()
        .optional()
        .describe("Assembly Code written on the excavation types, for example A9010.10.02."),
      fillCode: z
        .string()
        .optional()
        .describe("Assembly Code written on the backfill types, for example A9010.10.05."),
      fillParameterName: z
        .string()
        .optional()
        .describe(
          "Volume parameter on the masses to write the resulting fill volume into. Must already " +
            "exist and be bound to Toposolid."
        ),
      originParameterName: z
        .string()
        .optional()
        .describe(
          "Text parameter to record whether the mass came from a footing or a foundation beam, " +
            "useful for grouping a schedule."
        ),
      cutColumns: z
        .boolean()
        .optional()
        .describe("Also cut columns out of the backfill. Defaults to true."),
      replaceExisting: z
        .boolean()
        .optional()
        .describe("Delete masses from a previous run before building new ones. Defaults to true."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_foundation_earthworks", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `create foundation earthworks failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
