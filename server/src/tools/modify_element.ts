import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerModifyElementTool(server: McpServer) {
  server.tool(
    "modify_element",
    "Modify one or more parameters of an existing Revit element, identified by its element ID. " +
      "Each change names a parameter and its new value; the add-in converts the value to the " +
      "parameter's StorageType (String, Integer, Double or ElementId). All changes are applied in a " +
      "single transaction, so one undo reverts the whole call. Parameters that do not exist or are " +
      "read-only are reported individually with an error status instead of failing the whole call. " +
      "Numeric (Double) parameters use Revit internal units (feet) by default; set useDisplayUnits " +
      "to true to pass the value in the project's display units instead (for example \"3000\" for mm). " +
      "Use get_parameter first when you are unsure of a parameter's exact name or storage type.",
    {
      elementId: z
        .number()
        .int()
        .describe("Revit element ID of the element to modify"),
      changes: z
        .array(
          z.object({
            parameterName: z
              .string()
              .describe(
                "Exact name of the parameter to modify, as shown in the Revit properties palette"
              ),
            value: z
              .union([z.string(), z.number(), z.boolean()])
              .describe(
                "New value. Booleans map to Yes/No parameters. Doubles are in internal units " +
                  "(feet) unless useDisplayUnits is true."
              ),
            useDisplayUnits: z
              .boolean()
              .optional()
              .describe(
                "Only affects Double parameters. When true, the value is parsed using the " +
                  "project's display units (mm, m, etc.) instead of Revit internal feet."
              ),
          })
        )
        .min(1)
        .describe("List of parameter changes to apply to the element"),
    },
    async (args, extra) => {
      const params = {
        elementId: args.elementId,
        changes: args.changes.map((change) => ({
          parameterName: change.parameterName,
          value: String(change.value),
          useDisplayUnits: change.useDisplayUnits ?? false,
        })),
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("modify_element", params);
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
              text: `modify element failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
