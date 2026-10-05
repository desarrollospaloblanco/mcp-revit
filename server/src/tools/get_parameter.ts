import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetParameterTool(server: McpServer) {
  server.tool(
    "get_parameter",
    "Read parameters from a Revit element. When parameterName is given, only that parameter is " +
      "returned; otherwise every parameter on the element is returned. Each parameter reports its " +
      "name, raw internal value (value), formatted display value (valueString), storageType, " +
      "isReadOnly and hasValue. Doubles come back in Revit internal units (feet) in value, and in " +
      "the project's display units in valueString. Call this before modify_element or set_parameter " +
      "to confirm a parameter's exact name, storage type and whether it can be written.",
    {
      elementId: z
        .number()
        .int()
        .describe("Revit element ID to read parameters from"),
      parameterName: z
        .string()
        .optional()
        .describe(
          "Name of a single parameter to read. Omit to return every parameter on the element."
        ),
    },
    async (args, extra) => {
      const params = {
        elementId: args.elementId,
        parameterName: args.parameterName,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_parameter", params);
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
              text: `get parameter failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
