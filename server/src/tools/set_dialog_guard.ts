import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSetDialogGuardTool(server: McpServer) {
  server.tool(
    "set_dialog_guard",
    "Switch on or off a handler inside the Revit plugin that answers modal dialogs (failure dialogs, " +
      "'Dialog_Revit_DocWarnDialog', join walls questions) so API calls are never left blocked waiting " +
      "for a click. Turn it on before a batch of edits and off when done; it stays on until switched off " +
      "or Revit restarts, and it also answers dialogs raised by the user's own work in the meantime. " +
      "Calling it again only updates the settings, it never adds a second handler, so do not subscribe " +
      "DialogBoxShowing or FailuresProcessing from send_code_to_revit. Cancel on an error dialog rolls back " +
      "the transaction that raised it. Every answer is recorded; read it with get_dialog_log. If Revit is " +
      "already blocked by a dialog, the call times out and applies once Revit is idle again.",
    {
      enabled: z.boolean().describe("true to switch the guard on, false to switch it off."),
      policy: z
        .enum(["no_for_join_questions", "cancel", "ok"])
        .optional()
        .describe(
          "How to answer. 'no_for_join_questions' (default): 'No' to questions about joining or attaching " +
            "walls or geometry, 'Cancel' to every other dialog. 'cancel': 'Cancel' to everything. " +
            "'ok': 'OK' (or 'Yes') to everything, which accepts warnings and keeps the edit."
        ),
      deleteWarnings: z
        .boolean()
        .optional()
        .describe(
          "Also delete warnings during failure processing so they never reach a dialog. Errors are never " +
            "deleted. Defaults to false."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_dialog_guard", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `set dialog guard failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
