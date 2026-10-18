import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { listRevitInstances, resolvePort, selectPort } from "../utils/RevitInstances.js";

export function registerRevitInstanceTools(server: McpServer) {
  server.tool(
    "list_revit_instances",
    "List the Revit sessions on this machine that have the MCP service switched on: process id, port, Revit version " +
      "and open documents, plus the port the tools are using now. Each session takes the first free port from 8080.",
    {},
    async () => {
      const instances = listRevitInstances();
      return {
        content: [
          {
            type: "text",
            text: JSON.stringify({ currentPort: resolvePort(), instances }, null, 2),
          },
        ],
      };
    }
  );

  server.tool(
    "select_revit_instance",
    "Choose which Revit session the tools talk to for the rest of this conversation, by port, process id or the title " +
      "of a document open in it. Call with no arguments to go back to automatic selection (the only running session, " +
      "otherwise port 8080).",
    {
      port: z.number().int().optional().describe("Port of the session."),
      pid: z.number().int().optional().describe("Process id of the Revit session."),
      documentTitle: z.string().optional().describe("Title (or part of it) of a document open in the session."),
    },
    async (args) => {
      const instances = listRevitInstances();
      if (args.port === undefined && args.pid === undefined && !args.documentTitle) {
        selectPort(undefined);
        return { content: [{ type: "text", text: `Automatic selection; now using port ${resolvePort()}` }] };
      }

      const match = instances.find(
        (i) =>
          (args.port !== undefined && i.port === args.port) ||
          (args.pid !== undefined && i.pid === args.pid) ||
          (args.documentTitle !== undefined &&
            i.documents.some((d) => d.toLowerCase().includes(args.documentTitle!.toLowerCase())))
      );
      if (!match) {
        return {
          content: [
            { type: "text", text: `No running session matches. Sessions: ${JSON.stringify(instances, null, 2)}` },
          ],
        };
      }

      selectPort(match.port);
      return {
        content: [
          {
            type: "text",
            text: `Using Revit ${match.revitVersion} (pid ${match.pid}) on port ${match.port}. Documents: ${match.documents.join(", ")}`,
          },
        ],
      };
    }
  );
}
