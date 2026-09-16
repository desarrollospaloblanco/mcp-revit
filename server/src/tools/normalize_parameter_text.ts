import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerNormalizeParameterTextTool(server: McpServer) {
  server.tool(
    "normalize_parameter_text",
    "Normalize the text case of a parameter across element types, instances and materials, for example forcing every Description to uppercase. Read only parameters and empty values are skipped. Run it with dryRun first to see what would change.",
    {
      parameterName: z
        .string()
        .describe("Name of the text parameter to normalize, exactly as it appears in Revit (for example 'Description')"),
      textCase: z
        .enum(["Upper", "Lower", "Title", "Sentence", "None"])
        .optional()
        .default("Upper")
        .describe("Target case. 'None' only applies trim and removeAccents"),
      targets: z
        .array(z.enum(["ElementType", "Instance", "Material"]))
        .optional()
        .describe("Where to look for the parameter. Defaults to element types and materials"),
      categories: z
        .array(z.string())
        .optional()
        .describe("Limit to these Revit category names, for example ['Walls','Floors']. Omit for the whole model. Materials are not filtered by category"),
      removeAccents: z
        .boolean()
        .optional()
        .default(false)
        .describe("Strip accents, turning CIMENTACIÓN into CIMENTACION. Off by default, since accented capitals are correct in Spanish"),
      trim: z
        .boolean()
        .optional()
        .default(false)
        .describe("Also remove leading and trailing whitespace"),
      onlyInUse: z
        .boolean()
        .optional()
        .default(false)
        .describe("For element types, skip those without placed instances, which leaves untouched the unused template content"),
      dryRun: z
        .boolean()
        .optional()
        .default(false)
        .describe("Report what would change without writing anything to the model"),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("normalize_parameter_text", args);
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
              text: `Parameter normalization failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
