import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreateGridRailingsTool(server: McpServer) {
  server.tool(
    "create_grid_railings",
    "Trace the grid with railings so the setting-out run can be quantified. Revit has no element " +
      "that measures the length of a grid layout, but a railing reports its length and schedules " +
      "like anything else, so a stripped railing type — one rail, no balusters, no posts — placed " +
      "along each grid line turns the layout into a quantity. Each line is trimmed to its " +
      "intersections with the outermost grids running the other way, giving a closed grid rather " +
      "than the full extent of every line. Intersections are solved as infinite lines, so a " +
      "non-orthogonal grid works too. Re-running replaces the previous trace.",
    {
      documentTitle: z
        .string()
        .optional()
        .describe("Exact title of the open document to act on. Omit to use the active document."),
      typeName: z
        .string()
        .optional()
        .describe(
          "Name of the railing type to create or reuse. Defaults to 'TRAZO DE EJES'. It is " +
            "duplicated from any existing railing type and then stripped of rails, balusters and posts."
        ),
      baseOffsetMeters: z
        .number()
        .optional()
        .describe(
          "Vertical offset from the host level, in metres. Defaults to -20 so the trace sits well " +
            "below the model and does not clutter any view."
        ),
      levelName: z
        .string()
        .optional()
        .describe("Level to host the railings on. Defaults to the lowest level in the document."),
      trimToOuterGrids: z
        .boolean()
        .optional()
        .describe(
          "Trim each grid line to its first and last crossing grid, closing the grid. Defaults to " +
            "true. With false the command has nothing to trim against and skips the line."
        ),
      commentPrefix: z
        .string()
        .optional()
        .describe(
          "Prefix for the Comments value that names each line, so the schedule shows a row per grid. " +
            "Defaults to 'EJE '."
        ),
      assemblyCode: z
        .string()
        .optional()
        .describe("Assembly Code to write on the railing type, for example A1010.90.01."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_grid_railings", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `create grid railings failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
