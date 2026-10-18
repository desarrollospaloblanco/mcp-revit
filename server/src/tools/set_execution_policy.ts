import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSetExecutionPolicyTool(server: McpServer) {
  server.tool(
    "set_execution_policy",
    "Read or change how send_code_to_revit treats model changes, for every Revit session on this machine. " +
      "'auto' (default): changes are applied as one undo step. 'ask': Revit isolates the changed elements and waits " +
      "for the user to click Apply or Discard. 'readonly': every run is a dry run and nothing is kept. " +
      "Call without a policy to read the current one. Change it only when the user asks.",
    {
      policy: z.enum(["auto", "ask", "readonly"]).optional().describe("New policy; omit to read the current one."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_execution_policy", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `set execution policy failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
