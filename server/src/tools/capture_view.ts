import fs from "fs";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCaptureViewTool(server: McpServer) {
  server.tool(
    "capture_view",
    "See the model: returns a PNG image of a view. view='3d' (default) builds a temporary isometric view, framed by a " +
      "section box around elementIds when given, seen from a direction, and rolls it back after the export so nothing " +
      "stays in the model. Any other value exports an existing view by name or id. Use it to check work visually after " +
      "a modelling step. The file is also kept 14 days under %APPDATA%\\RevitMCP\\captures.",
    {
      view: z.string().optional().describe("'3d' for a temporary view, or the name or id of an existing view. Defaults to '3d'."),
      elementIds: z.array(z.number()).optional().describe("For view '3d': frame the view on these elements with a section box."),
      direction: z
        .enum(["southwest", "southeast", "northwest", "northeast", "north", "south", "east", "west", "top"])
        .optional()
        .describe("For view '3d': where the camera stands. Defaults to southwest."),
      style: z
        .enum(["shaded", "realistic", "hidden", "consistent", "wireframe"])
        .optional()
        .describe("For view '3d': display style. Defaults to shaded with edges."),
      paddingMeters: z.number().min(0).max(100).optional().describe("Margin around elementIds for the section box. Defaults to 2."),
      width: z.number().int().min(400).max(4000).optional().describe("Image width in pixels. Defaults to 1600."),
      documentTitle: z.string().optional().describe("Document to capture; defaults to the active one."),
    },
    async (args) => {
      try {
        const response = (await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("capture_view", args, 200000);
        })) as { imagePath: string; document: string; view: string; width: number; height: number };

        const data = fs.readFileSync(response.imagePath).toString("base64");
        return {
          content: [
            { type: "image", data, mimeType: "image/png" },
            {
              type: "text",
              text: `${response.document} · ${response.view} · ${response.width}x${response.height} px · ${response.imagePath}`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            { type: "text", text: `capture failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
        };
      }
    }
  );
}
