import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerLookupRevitApiTool(server: McpServer) {
  server.tool(
    "lookup_revit_api",
    "Look up a Revit API type or member in the Revit that is running, before writing send_code_to_revit code: " +
      "namespace and using line, constructors, methods, properties, static fields, enum values, and a creation list " +
      "(static factories and doc.Create.New… methods that return the type). A name that does not exist returns " +
      "found=false with the closest type or member names and enum values with that name. " +
      "Examples: 'Wall', 'Wall.Create', 'Autodesk.Revit.DB.Floor', 'BuiltInParameter.WALL_BASE_OFFSET', 'RBS_PIPE_SLOPE'.",
    {
      name: z.string().describe("Type, Type.Member, full type name, or a bare enum value name."),
      member: z.string().optional().describe("Only members whose name contains this text."),
      maxMembers: z.number().int().min(5).max(400).optional().describe("Maximum members per list. Defaults to 80."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("lookup_revit_api", args);
        });
        return { content: [{ type: "text", text: JSON.stringify(response, null, 2) }] };
      } catch (error) {
        return {
          content: [
            { type: "text", text: `lookup failed: ${error instanceof Error ? error.message : String(error)}` },
          ],
        };
      }
    }
  );
}
