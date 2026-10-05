import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCopyRampsAsFloorsTool(server: McpServer) {
  server.tool(
    "copy_ramps_as_floors",
    "Replicate every ramp in the model as a sloped floor of a given type, so ramps land in a slab " +
      "take-off. The outline and the slope are measured from the ramp's real top face, not from its " +
      "type name — names like '17%' often encode a 1:17 ratio rather than a percentage, so they " +
      "disagree with the geometry. Requires Revit 2022 or newer. Ramps whose outline contains arcs " +
      "are skipped and reported, because projecting an arc on a sloped plane down to plan turns it " +
      "into an ellipse and would distort the footprint.",
    {
      floorTypeName: z
        .string()
        .describe("Exact name of the floor type to create the copies with."),
      documentTitle: z
        .string()
        .optional()
        .describe("Exact title of the open document to act on. Omit to use the active document."),
      comment: z
        .string()
        .optional()
        .describe(
          "Value written to Comments on each new floor so they can be filtered apart from real " +
            "slabs. Defaults to 'ramp'. Pass an empty string to leave Comments untouched."
        ),
      structural: z
        .boolean()
        .optional()
        .describe("Mark the new floors as structural. Defaults to true."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("copy_ramps_as_floors", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `copy ramps as floors failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
