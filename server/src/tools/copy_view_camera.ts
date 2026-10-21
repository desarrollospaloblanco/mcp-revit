import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { writeOptions } from "../utils/revitTool.js";

export function registerCopyViewCameraTool(server: McpServer) {
  server.tool(
    "copy_view_camera",
    "Copy the camera of a 3D view to the view with the same name in other open documents (created when missing): " +
      "orientation, absolute crop box, section box, display style, detail level and far clip. Use it so two design " +
      "options are seen from exactly the same place and with the same framing.",
    {
      view: z.string().describe("Name or id of the source 3D view."),
      targetDocuments: z.array(z.string()).min(1).describe("Titles of the documents to copy to."),
      sourceDocument: z.string().optional().describe("Document of the source view; defaults to the active one."),
      copySectionBox: z.boolean().optional().describe("Also copy the section box. Defaults to true."),
      runMark: writeOptions.runMark,
      swallowWarnings: writeOptions.swallowWarnings,
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("copy_view_camera", args, 140000);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            { type: "text", text: `copy_view_camera failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
        };
      }
    }
  );
}
