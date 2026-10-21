import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit } from "../utils/revitTool.js";

export function registerTransferViewTemplatesTool(server: McpServer) {
  server.tool(
    "transfer_view_templates",
    "Copy view templates (with their filters) from one open model to others so several models share the same " +
      "graphics. A template that exists in a target is replaced: views using it switch to the copy, the old one is " +
      "deleted and the copy takes its name. Reports added and replaced templates and views switched per model.",
    {
      sourceDocument: z.string().optional().describe("Defaults to the active document."),
      targetDocuments: z.array(z.string()).min(1),
      templates: z.array(z.string()).optional().describe("Template names; all templates when omitted."),
      replaceExisting: z.boolean().optional().describe("Defaults to true."),
      swallowWarnings: z.boolean().optional(),
    },
    async (args) => callRevit("transfer_view_templates", args, 1820000)
  );
}
