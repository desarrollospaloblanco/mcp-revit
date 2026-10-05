import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportViewImageTool(server: McpServer) {
  server.tool(
    "export_view_image",
    "Export one view to a PNG file and return its path, to check a model visually against its drawings. " +
      "Pick the view by name, by level (its floor plan) or as the 3D view; the active view is never used. " +
      "If the document has no 3D view, an isometric one named 'MCP 3D' is created.",
    {
      documentTitle: z.string().optional().describe("Exact title of the open document. Omit for the active one."),
      view: z.string().optional().describe("Exact name of the view to export."),
      level: z.string().optional().describe("Level name; exports that level's floor plan."),
      threeD: z.boolean().optional().describe("Export the 3D view."),
      outputPath: z.string().optional().describe("Where to write the PNG. Defaults to %TEMP%\\revit-mcp."),
      pixelSize: z.number().int().optional().describe("Width of the image in pixels. Defaults to 2400."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_view_image", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `export view image failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
