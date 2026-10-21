import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerExportViewsTool(server: McpServer) {
  server.tool(
    "export_views",
    "Export several views to PNG in one call, from one or several open documents, at a given pixel width. Each " +
      "entry names a view (name or id) and optionally its document and the file name. Failures are reported per " +
      "view without stopping the rest. Returns the image paths.",
    {
      views: z
        .array(
          z.object({
            view: z.string().describe("View name or element id."),
            document: z.string().optional().describe("Document title; defaults to the active one."),
            fileName: z.string().optional().describe("File name prefix; Revit appends the view type and name."),
          })
        )
        .min(1),
      width: z.number().int().min(200).max(15000).optional().describe("Pixel width. Defaults to 3000."),
      outputFolder: z.string().optional().describe("Folder for the PNGs; defaults to %APPDATA%\\RevitMCP\\exports."),
    },
    async (args) => {
      try {
        const timeout = Math.min(3600, 60 + 90 * args.views.length) * 1000 + 20000;
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_views", args, timeout);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [{ type: "text", text: `export_views failed: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
