import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";

export function registerCopyElementsTool(server: McpServer) {
  server.tool(
    "copy_elements",
    "Copy elements. levels: copy the selection from sourceLevel to each targetLevel; every copy is re-hosted on its " +
      "target level at the same height above it (walls map their top level by the same shift), and hosted doors and " +
      "windows travel with their walls. documents: copy the selection to another open document at the same " +
      "coordinates, using the target's types when names clash. view: copy view-specific elements (annotation, " +
      "detail lines, legend content) from sourceView to targetViews, also into another document; clearTarget empties " +
      "the target views first. Ask before replicating a typical floor to other levels.",
    {
      mode: z.enum(["levels", "documents", "view"]).optional().describe("Defaults to levels."),
      ...selection,
      sourceLevel: z.string().optional(),
      targetLevels: z.array(z.string()).optional(),
      targetDocument: z.string().optional(),
      sourceView: z.string().optional(),
      targetViews: z.array(z.string()).optional(),
      clearTarget: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("copy_elements", args, 1820000)
  );
}
