import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCheckHostedOpeningsTool(server: McpServer) {
  server.tool(
    "check_hosted_openings",
    "Read-only review of doors and windows against their host walls. Issues: unhosted (no host), hostNotWall, " +
      "notCutting (the wall is solid through the opening centre, so the opening does not cut it), nearWallEnd (the " +
      "opening reaches the end of its wall), and for doors wallCrossesOpening (another wall meets the door opening) " +
      "and overlappingWall (a duplicated wall inside the host thickness over the opening). Returns counts per issue " +
      "and the elements with id, level, type, host and point in metres. Curtain wall doors are skipped. Logic checks " +
      "(swing side, doors joining two apartments) still need a look at the plan.",
    {
      documentTitle: z.string().optional().describe("Document to review; defaults to the active one."),
      levels: z.array(z.string()).optional().describe("Only these level names; all levels when omitted."),
      categories: z.array(z.enum(["doors", "windows"])).optional().describe("Defaults to both."),
      endToleranceMeters: z.number().min(0).max(1).optional().describe("Clearance to the wall end for nearWallEnd. Defaults to 0.05."),
      limit: z.number().int().min(1).max(5000).optional().describe("Maximum issues listed. Defaults to 300."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_hosted_openings", args, 620000);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            { type: "text", text: `check_hosted_openings failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
        };
      }
    }
  );
}
