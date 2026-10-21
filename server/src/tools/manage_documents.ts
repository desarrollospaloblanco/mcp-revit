import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit } from "../utils/revitTool.js";

export function registerManageDocumentsTool(server: McpServer) {
  server.tool(
    "manage_documents",
    "Open documents in Revit. list: title, path, active, modified, workshared, detached. open: open a .rvt in the " +
      "background (or activate=true to open it in a window; detachFromCentral opens a detached copy). new: a blank " +
      "metric project, or from a template path. activate: bring an open, saved document to the front. close: close a " +
      "background document without saving; a modified one needs discardChanges=true. Opening a central or local file " +
      "and closing documents affect the user's session: confirm with the user first.",
    {
      action: z.enum(["list", "open", "new", "activate", "close"]).optional().describe("Defaults to list."),
      path: z.string().optional().describe("For open: full path of the .rvt or .rfa."),
      activate: z.boolean().optional().describe("For open: open in a window and make it active."),
      detachFromCentral: z.boolean().optional().describe("For open: detach from central, preserving worksets."),
      template: z.string().optional().describe("For new: template .rte path; a blank metric project when omitted."),
      documentTitle: z.string().optional().describe("For activate and close: the document title."),
      discardChanges: z.boolean().optional().describe("For close: allow closing with unsaved changes (they are lost)."),
      timeoutSeconds: z.number().int().min(30).max(3600).optional().describe("Defaults to 900 (opening big models takes time)."),
    },
    async (args) => callRevit("manage_documents", args, ((args.timeoutSeconds ?? 900) + 20) * 1000)
  );
}
