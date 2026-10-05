import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerFormatSchedulesTool(server: McpServer) {
  server.tool(
    "format_schedules",
    "Rename schedule column headings and apply a consistent look: bold title and heading row on a " +
      "coloured background, plain body, numeric columns aligned right. Useful for translating " +
      "headings into the project language in one pass. Optionally highlights the first column as a " +
      "row header. Pass scheduleNames to limit the scope, otherwise every non-template schedule in " +
      "the document is formatted.",
    {
      documentTitle: z
        .string()
        .optional()
        .describe("Exact title of the open document to act on. Omit to use the active document."),
      headings: z
        .record(z.string())
        .optional()
        .describe(
          "Map of field name to the heading to show, for example {\"Assembly Code\": \"CÓDIGO\", " +
            "\"Volume\": \"VOLUMEN\"}. Fields not listed keep their current heading."
        ),
      scheduleNames: z
        .array(z.string())
        .optional()
        .describe("Only format these schedules. Omit to format all of them."),
      fontName: z.string().optional().describe("Font for every cell. Defaults to Arial."),
      headerColor: z
        .array(z.number())
        .optional()
        .describe("RGB background for the heading row, as three numbers. Defaults to [47, 84, 150]."),
      titleColor: z
        .array(z.number())
        .optional()
        .describe("RGB background for the schedule title. Defaults to [31, 56, 100]."),
      highlightFirstColumn: z
        .boolean()
        .optional()
        .describe(
          "Give the first column the heading colours so it reads as a row header. Defaults to true."
        ),
      alignNumbersRight: z
        .boolean()
        .optional()
        .describe(
          "Right-align area, volume, length and similar columns so decimals line up, and left-align " +
            "the rest. Defaults to true."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("format_schedules", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `format schedules failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
