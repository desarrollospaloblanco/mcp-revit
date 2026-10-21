import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerEnsureParameterTool(server: McpServer) {
  server.tool(
    "ensure_parameter",
    "Make sure a project parameter exists and is bound to some categories, e.g. 'Tipo de Apartamento' on OST_Rooms " +
      "and OST_Areas. The definition comes from a shared parameter file (yours, or one kept under " +
      "%APPDATA%\\RevitMCP), and Revit's shared parameter file setting is restored afterwards. If the parameter is " +
      "already bound, missing categories are added.",
    {
      name: z.string(),
      categories: z.array(z.string()).min(1).describe("BuiltInCategory names."),
      dataType: z.enum(["text", "number", "integer", "length", "area", "yesno"]).optional().describe("Defaults to text."),
      instance: z.boolean().optional().describe("Instance (default) or type parameter."),
      sharedParameterFile: z.string().optional(),
      sharedGroup: z.string().optional().describe("Group in the shared parameter file. Defaults to MCP."),
      ...writeOptions,
    },
    async (args) => callRevit("ensure_parameter", args, 320000)
  );
}
