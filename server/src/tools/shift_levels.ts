import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";

export function registerShiftLevelsTool(server: McpServer) {
  server.tool(
    "shift_levels",
    "Move levels by one offset and/or rename (renumber) levels, in one transaction. Select the levels to move with " +
      "fromElevationMeters (every level at or above it, 5 mm tolerance, compared with the level's Elevation) or levels " +
      "(names). renames {oldName: newName} applies to any level and goes through temporary names, so N2->N4 with " +
      "N4->N6 works. With renameViews (default true) plan, ceiling and area plan views of a renamed level whose name is " +
      "the level name, or starts with it followed by a non-alphanumeric character ('N2 - Oficinas'), are renamed with it. " +
      "Everything is checked first (unknown names, a moved level landing within 0.01 m of or crossing a level that does " +
      "not move, duplicate names, view name clashes) and nothing changes if any check fails. Levels move top-down when " +
      "raising and bottom-up when lowering. The result lists every level before and after, the views renamed, and " +
      "notFollowing: walls with unconnected height based below a moved level that reach it (they keep their height), and " +
      "walls or columns with one end on a moved level and the other on a level that stays (their height changes). " +
      "build_model_from_spec keys that still carry old level names come back as a warning; they are not rewritten. " +
      "dryRun reports and rolls back.",
    {
      fromElevationMeters: z
        .number()
        .optional()
        .describe("Move every level at or above this elevation. Use this or levels, not both."),
      levels: z.array(z.string()).optional().describe("Names of the levels to move."),
      offsetMeters: z
        .number()
        .optional()
        .describe("Positive raises, negative lowers. Defaults to 0 (rename only)."),
      renames: z
        .record(z.string())
        .optional()
        .describe("{oldName: newName}, e.g. {N2: 'N4', N3: 'N5'}; any level, moved or not."),
      renameViews: z.boolean().optional().describe("Rename the plan views that carry the level name. Defaults to true."),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("shift_levels", args, 920000)
  );
}
