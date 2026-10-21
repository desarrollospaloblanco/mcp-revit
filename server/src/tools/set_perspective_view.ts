import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const point = z.array(z.number()).length(3);

export function registerSetPerspectiveViewTool(server: McpServer) {
  server.tool(
    "set_perspective_view",
    "Create or update a presentation 3D view from an eye point and a target point (metres, project coordinates). " +
      "Two-point perspective by default (horizontal view direction, so vertical edges stay vertical). Hides levels, " +
      "grids, lines, room separation lines, cameras and imports, turns off the far clip and frames the model: the " +
      "view is exported once at low resolution, the extent of the model in the image is measured and mapped back to " +
      "the crop, plus a margin. Pass several documents to get the SAME camera and the SAME crop in all of them (the " +
      "union of the framings), which is how design options are compared. Optionally exports the final PNG.",
    {
      viewName: z.string().describe("Name of the 3D view; reused when it exists."),
      eye: point.describe("Camera position [x, y, z] in metres."),
      target: point.describe("Point the camera looks at [x, y, z] in metres."),
      documents: z.array(z.string()).optional().describe("Document titles; defaults to the active one."),
      twoPoint: z.boolean().optional().describe("Keep the view direction horizontal. Defaults to true."),
      perspective: z.boolean().optional().describe("Perspective (default) or isometric when the view is created."),
      sectionBox: z
        .object({ min: point, max: point })
        .optional()
        .describe("Section box in metres, e.g. to cut basements below the street."),
      style: z.enum(["realistic", "shaded", "hidden", "consistent", "wireframe"]).optional().describe("Defaults to realistic."),
      frame: z
        .enum(["fit", "project", "keep"])
        .optional()
        .describe("fit: calibrated framing (default). project: geometric projection only. keep: leave the crop."),
      marginPercent: z.number().min(0).max(50).optional().describe("Margin around the model. Defaults to 4."),
      aspectRatio: z.number().positive().optional().describe("Width/height of the crop, e.g. 1.7778 for 16:9."),
      hideImports: z.boolean().optional().describe("Hide linked/imported CAD. Defaults to true."),
      exportWidth: z.number().int().min(400).max(15000).optional().describe("Export the final PNG at this width."),
      outputFolder: z.string().optional().describe("Folder for the PNG; defaults to %APPDATA%\\RevitMCP\\exports."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_perspective_view", args, 620000);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            { type: "text", text: `set_perspective_view failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
        };
      }
    }
  );
}
