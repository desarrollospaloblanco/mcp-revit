import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerCopyDimensionsTool(server: McpServer) {
  server.tool(
    "copy_dimensions",
    "Rebuild the linear dimensions of a source plan in other plans, e.g. a typical floor's dimensioned plan on the " +
      "other levels. Grids and elements visible in both views keep their references; wall and column faces are " +
      "matched to the element of the same category in the target view lying in the same plane. Reports created, " +
      "lost and unmatched references per view. replaceExisting deletes the target's dimensions first.",
    {
      sourceView: z.string(),
      targetViews: z.array(z.string()).min(1),
      replaceExisting: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("copy_dimensions", args, 920000)
  );
}
