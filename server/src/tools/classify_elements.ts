import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerClassifyElementsTool(server: McpServer) {
  server.tool(
    "classify_elements",
    "Assign Assembly Code to element types and fill in a missing Type Mark from the type name. " +
      "Rules are evaluated in order and the first match wins, so put the specific ones first. Only " +
      "types with instances in the model are touched. The result reports the Assembly Description " +
      "Revit resolved for each code: an empty one means the code is not in the classification file " +
      "loaded in that project, which is the quickest way to catch a wrong code.",
    {
      documentTitle: z
        .string()
        .optional()
        .describe(
          "Exact title of the open document to act on. Omit to use the active document."
        ),
      rules: z
        .array(
          z.object({
            categories: z
              .array(z.string())
              .describe("Categories this rule applies to, for example ['StructuralColumns']."),
            typeNameStartsWith: z
              .string()
              .optional()
              .describe("Only types whose name starts with this text."),
            typeNameContains: z
              .string()
              .optional()
              .describe("Only types whose name contains this text."),
            assemblyCode: z.string().describe("Assembly Code to write, for example B1010.10.05."),
          })
        )
        .describe("Classification rules, most specific first."),
      deriveTypeMark: z
        .boolean()
        .optional()
        .describe(
          "Fill an empty Type Mark from the type name: strip a known prefix, then take everything " +
            "up to the first underscore, so 'WAD_EXP+MI-11_0.15m' becomes 'MI-11'. Existing marks are " +
            "never overwritten. Defaults to true."
        ),
      stripPrefixes: z
        .array(z.string())
        .optional()
        .describe("Prefixes to strip before deriving the mark. Defaults to ['WAD_EXP+', 'WAD_']."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("classify_elements", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `classify elements failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
