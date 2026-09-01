import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSetParameterTool(server: McpServer) {
  server.tool(
    "set_parameter",
    "Set a single parameter to the same value on one or many Revit elements. Prefer this over " +
      "modify_element for batch edits such as tagging a whole selection with the same comment or " +
      "mark. To change several different parameters on one element, use modify_element instead. " +
      "All elements are written in a single transaction, so one undo reverts the whole call, and " +
      "each element reports its own ok/error status. Double parameters use Revit internal units " +
      "(feet) unless useDisplayUnits is true.",
    {
      elementIds: z
        .array(z.number().int())
        .min(1)
        .describe("Revit element IDs to write the parameter on"),
      parameterName: z
        .string()
        .describe(
          "Exact name of the parameter to set, as shown in the Revit properties palette"
        ),
      value: z
        .union([z.string(), z.number(), z.boolean()])
        .describe(
          "New value applied to every element. Booleans map to Yes/No parameters. Doubles are in " +
            "internal units (feet) unless useDisplayUnits is true."
        ),
      useDisplayUnits: z
        .boolean()
        .optional()
        .describe(
          "Only affects Double parameters. When true, the value is parsed using the project's " +
            "display units (mm, m, etc.) instead of Revit internal feet."
        ),
    },
    async (args, extra) => {
      const params = {
        elementIds: args.elementIds,
        parameterName: args.parameterName,
        value: String(args.value),
        useDisplayUnits: args.useDisplayUnits ?? false,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_parameter", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `set parameter failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
