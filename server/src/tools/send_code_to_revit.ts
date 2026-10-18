import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const transactionModeSchema = z
  .enum(["auto", "none"])
  .default("auto")
  .describe(
    "How the snippet should interact with Revit transactions. Use 'auto' to wrap the snippet in a transaction, or 'none' when the called code manages its own transactions."
  );

export function registerSendCodeToRevitTool(server: McpServer) {
  server.tool(
    "send_code_to_revit",
    "Send C# code to Revit for execution. The code will be inserted into a template with access to the Revit Document and parameters. Your code should be written to work within the Execute method of the template. " +
      "Each run is wrapped in a transaction group on every open document, so all its changes become one undo step and the response lists what was created, modified and deleted per document. " +
      "Use dryRun=true to see that change summary without keeping anything. The machine-wide execution policy (set_execution_policy) can require approval in Revit or make every run a dry run. " +
      "Scripts that save, synchronize, open or close documents must pass group=false. Every run is recorded for 14 days (get_run_history, get_run).",
    {
      code: z
        .string()
        .describe(
          "The C# code to execute in Revit. This code will be inserted into the Execute method of a template with access to Document and parameters."
        ),
      parameters: z
        .array(z.string())
        .optional()
        .describe(
          "Optional execution parameters that will be passed to your code"
        ),
      transactionMode: transactionModeSchema,
      dryRun: z
        .boolean()
        .optional()
        .describe("Run the code, report the changes it would make, then roll everything back. Defaults to false."),
      group: z
        .boolean()
        .optional()
        .describe(
          "Wrap the run in transaction groups (one undo step, change summary, rollback on failure). Defaults to true; set false only for code that saves, synchronizes, opens or closes documents."
        ),
      title: z
        .string()
        .optional()
        .describe("Short name of what the run does. It names the undo step and the run record, and appears in the approval prompt."),
      timeoutSeconds: z
        .number()
        .int()
        .min(5)
        .max(3600)
        .optional()
        .describe("How long to wait for Revit. Defaults to 60 s, or 600 s under the 'ask' policy."),
    },
    async (args) => {
      const params = {
        code: args.code,
        parameters: args.parameters || [],
        transactionMode: args.transactionMode,
        dryRun: args.dryRun ?? false,
        group: args.group ?? true,
        title: args.title,
        timeoutSeconds: args.timeoutSeconds,
      };
      // The plugin enforces timeoutSeconds; the socket waits a little longer so it receives the
      // plugin's own timeout message instead of dropping the connection first.
      const socketTimeoutMs = ((args.timeoutSeconds ?? 600) + 15) * 1000;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("send_code_to_revit", params, socketTimeoutMs);
        });

        return {
          content: [
            {
              type: "text",
              text: `Code execution successful!\nResult: ${JSON.stringify(
                response,
                null,
                2
              )}`,
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Code execution failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
