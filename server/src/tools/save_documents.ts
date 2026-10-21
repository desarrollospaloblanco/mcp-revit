import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSaveDocumentsTool(server: McpServer) {
  server.tool(
    "save_documents",
    "Save open documents, and with synchronize=true synchronize workshared ones with central (comment, relinquish, " +
      "save local before and after). Runs outside any transaction, which is what Revit needs to save. By default " +
      "only modified documents are saved. Skips linked, never-saved and detached documents, and stops when the disk " +
      "has less than 2 GB free. If the API refuses to save the active document, the Save or Synchronize Now command " +
      "is posted to Revit (outcome posted); call again to check isModified. Saving also saves changes the user " +
      "made by hand, and synchronizing publishes them: ask before synchronizing.",
    {
      documents: z.array(z.string()).optional().describe("Document titles; defaults to every open, non-linked document."),
      synchronize: z.boolean().optional().describe("Synchronize workshared documents with central. Defaults to false."),
      comment: z.string().optional().describe("Synchronization comment. Defaults to MCP."),
      relinquish: z.boolean().optional().describe("Relinquish all borrowed elements and worksets. Defaults to true."),
      onlyModified: z.boolean().optional().describe("Skip documents without changes. Defaults to true."),
      timeoutSeconds: z.number().int().min(30).max(3600).optional().describe("Defaults to 900."),
    },
    async (args) => {
      try {
        const timeout = (args.timeoutSeconds ?? 900) * 1000 + 20000;
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("save_documents", args, timeout);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [{ type: "text", text: `save_documents failed: ${error instanceof Error ? error.message : String(error)}` }],
        };
      }
    }
  );
}
