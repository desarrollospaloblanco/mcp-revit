import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";

export function registerEditWallsTool(server: McpServer) {
  server.tool(
    "edit_walls",
    "Wall clean-up on selected walls (filter defaults to OST_Walls). operation: " +
      "align: move parallel walls so their axis, exterior or interior face lies on a target line ({axis:'x'|'y', value} " +
      "or {from, to} in metres); inserts move with them. snap: round end points to a module (default 0.005 m). " +
      "extend: move each end to where its axis meets another wall's axis within toleranceMeters (closes gaps and trims " +
      "overshoots). merge: join collinear touching walls of the same type (never deletes a wall with doors or windows). " +
      "dedupe: delete duplicated walls overlapping on the same axis, keeping the one with inserts. joins: allow or " +
      "disallow end joins. hierarchy: where a selected wall meets a wall of a higher rank, disallow its join at that end so " +
      "it stops at the higher wall's face (defaults: B-CN concrete 3 > BC block 2 > TY drywall 1; first matching rank wins, " +
      "rank 0 leaves a type out); summary.byTypes counts the ends per pair of types. locationLine: change the location line without moving the wall. flip: swap exterior and " +
      "interior. dryRun reports what would change and rolls back.",
    {
      operation: z.enum(["align", "snap", "extend", "merge", "dedupe", "joins", "hierarchy", "locationLine", "flip"]),
      ...selection,
      target: z
        .union([
          z.object({ axis: z.enum(["x", "y"]), value: z.number() }),
          z.object({ from: point2, to: point2 }),
        ])
        .optional()
        .describe("align: 'x' is the line x = value, 'y' the line y = value."),
      reference: z.enum(["axis", "exterior", "interior"]).optional().describe("align: which part goes on the line. Defaults to axis."),
      moduleMeters: z.number().positive().optional().describe("snap: defaults to 0.005."),
      toleranceMeters: z.number().positive().optional().describe("extend 0.3, merge 0.01, dedupe 0.02 by default."),
      joins: z.enum(["allow", "disallow"]).optional(),
      ends: z.enum(["both", "start", "end"]).optional(),
      ranks: z
        .array(
          z.object({
            typeStartsWith: z.string().optional(),
            typeContains: z.string().optional(),
            rank: z.number().int().min(0).describe("Higher wins; 0 leaves the type out of the hierarchy."),
          })
        )
        .optional()
        .describe("hierarchy: wall type ranks, first match wins, e.g. [{typeContains:'BC100', rank:0}, {typeStartsWith:'B-CN', rank:3}]."),
      locationLine: z
        .enum(["centerline", "coreCenterline", "finishExterior", "finishInterior", "coreExterior", "coreInterior"])
        .optional(),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("edit_walls", args, 920000)
  );
}
