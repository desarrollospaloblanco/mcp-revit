import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerMepReviewTool(server: McpServer) {
  server.tool(
    "mep_review",
    "Read-only MEP review of a Revit model, useful before coordination meetings. Checks: content (count per MEP " +
      "category), open_connectors (unconnected duct and pipe ends, by level), outside_systems (ducts and pipes in no " +
      "system), systems (HVAC and piping systems and their base equipment), uninsulated (ducts and pipes without " +
      "insulation, grouped by system type), slopes (sanitary pipes below the minimum slope, vertical stacks excluded) " +
      "and circuits (electrical devices with a connector but no circuit). Returns counts plus element ids, which " +
      "operate_element can select or isolate. Nothing is modified. Ask the user which systems need insulation and the " +
      "minimum drainage slope when it matters.",
    {
      checks: z
        .array(z.enum(["content", "open_connectors", "outside_systems", "systems", "uninsulated", "slopes", "circuits"]))
        .optional()
        .describe("Checks to run; all of them when omitted."),
      minSlopePercent: z.number().min(0).max(20).optional().describe("Minimum slope for sanitary pipes, in percent. Defaults to 1."),
      maxIds: z.number().int().min(10).max(1000).optional().describe("Maximum element ids per list. Defaults to 200."),
      documentTitle: z.string().optional().describe("Document to review; defaults to the active one."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("mep_review", args, 620000);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            { type: "text", text: `MEP review failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
        };
      }
    }
  );
}
