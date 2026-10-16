import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetDialogLogTool(server: McpServer) {
  server.tool(
    "get_dialog_log",
    "Return the dialogs the dialog guard answered (time, DialogId, message, answer given) and the guard's " +
      "current state. Entries with source 'failure' are warnings deleted before they reached a dialog. " +
      "A response starting with 'none' means Revit rejected every answer and the dialog may still be open. " +
      "It does not wait for Revit to be idle, so it also works while Revit is busy. Keeps the last 1000 " +
      "entries; the log is lost when Revit closes.",
    {
      limit: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Return only the most recent N entries. Omit for all of them."),
      clear: z
        .boolean()
        .optional()
        .describe("Empty the log after reading it. Defaults to false."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_dialog_log", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get dialog log failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
