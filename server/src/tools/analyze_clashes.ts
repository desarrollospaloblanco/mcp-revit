import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerAnalyzeClashesTool(server: McpServer) {
  server.tool(
    "analyze_clashes",
    "Find structural elements that overlap without being joined, and colour them in a view. " +
      "Joining resolves an overlap, so anything still reported as intersecting is genuinely " +
      "double-counted material. Same-category overlaps (column on column, beam on beam) are " +
      "duplicates or hard clashes and come back red; cross-category intersections that were never " +
      "joined come back yellow. Also reports the total overlapping volume, which is the amount " +
      "being counted twice in any take-off. The search runs against the document rather than a " +
      "view, so the result does not change when visibility changes.",
    {
      documentTitle: z
        .string()
        .optional()
        .describe("Exact title of the open document to act on. Omit to use the active document."),
      viewName: z
        .string()
        .optional()
        .describe(
          "View to colour the results in. Omit to use the active view, falling back to any 3D view. " +
            "Overrides are per view and do not change the elements themselves."
        ),
      categories: z
        .array(z.string())
        .optional()
        .describe(
          "Categories to analyse. Defaults to StructuralColumns, StructuralFraming, Floors and " +
            "StructuralFoundation."
        ),
      clearExisting: z
        .boolean()
        .optional()
        .describe("Clear previous overrides on those categories first. Defaults to true."),
      computeVolumes: z
        .boolean()
        .optional()
        .describe(
          "Compute the overlapping volume of each pair. Accurate but slower on large models. " +
            "Defaults to true."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("analyze_clashes", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `analyze clashes failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
