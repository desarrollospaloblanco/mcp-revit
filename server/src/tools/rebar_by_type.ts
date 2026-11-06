import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";
import { rebarCatalog, rebarPlacement } from "../utils/rebarOptions.js";

const barCount = z.object({
  bar: z.string(),
  count: z.number().int().optional(),
  hook: z.number().optional(),
});
const barSpacing = z.object({ bar: z.string(), spacing: z.number().optional() });
const cover = z
  .union([
    z.number(),
    z.object({ top: z.number().optional(), bottom: z.number().optional(), side: z.number().optional(), ends: z.number().optional() }),
  ])
  .optional()
  .describe("Clear cover in metres, one value or per face.");
const sheet = z
  .union([
    z.string(),
    z.object({ name: z.string(), wireMm: z.number().optional(), spacing: z.number().optional(), width: z.number().optional(), length: z.number().optional() }),
  ])
  .optional();

export function registerRebarByTypeTool(server: McpServer) {
  server.tool(
    "rebar_by_type",
    "Reinforce the concrete of the model from rules keyed by type mark or type name, reading each element's solid. kinds: " +
      "column (longitudinal bars around the section, hooked into the footing below, closed ties and cross-ties), beam " +
      "(top/middle/bottom bars extended and hooked at the ends, closed stirrups or links; also tie beams), footing (bottom " +
      "mats both ways with hooks up), stripFooting (longitudinal bars over transverse U bars), wallMesh and slabMesh (fabric " +
      "areas on the wall side or slab top, one or two layers). Each element takes the first rule that matches. Then it places " +
      "the steel like place_rebar: pieces of 6, 9 or 12 m, laps of 40 db away from joints, lap markers. Reinforcement that only " +
      "the drawings tell (bars drawn in elevations, slab bastones, dowels, stairs) goes through place_rebar.",
    {
      rules: z
        .array(
          z.object({
            kind: z.enum(["column", "beam", "footing", "stripFooting", "wallMesh", "slabMesh"]),
            match: z.string().describe("Type mark, type name or part of it (e.g. 'C-B', 'VA-1', 'Z-1', 'EM33')."),
            categories: z.array(z.string()).optional().describe("BuiltInCategory names, when not the kind's default."),
            label: z.string().optional().describe("Element label prefix (default Columna, Viga, Zapata, Cimiento corrido, Muro, Losa)."),
            group: z.string().optional().describe("Group for the schedules, e.g. 'Viga de amarre'. Defaults to the label."),
            cover,
            hostCover: z.number().optional().describe("Give the hosts this rebar cover in metres (thin slabs of 7 cm need 0.015)."),
            longitudinal: barCount
              .extend({ perFaceX: z.number().int().optional(), perFaceY: z.number().int().optional() })
              .optional()
              .describe("column: count around the section (even), or bars per face; stripFooting: count across the width."),
            ties: barSpacing
              .extend({ crossTies: z.boolean().optional(), start: z.number().optional() })
              .optional()
              .describe("column ties; crossTies default when a face has 3 or more bars."),
            hooks: z.enum(["both", "bottom", "top", "none"]).optional().describe("column bar hooks. Defaults to both."),
            hook: z.number().optional(),
            extendBelow: z.number().optional().describe("column bars below the base; by default into the footing below, on its mats."),
            top: z.array(barCount).optional().describe("beam top bars, e.g. [{bar:'#5', count:2}]."),
            middle: z.array(barCount).optional(),
            bottom: z.array(barCount).optional(),
            stirrups: barSpacing
              .extend({ shape: z.enum(["closed", "link"]).optional(), start: z.number().optional() })
              .optional(),
            extend: z.number().optional().describe("beam bars past the ends of the beam solid, metres. Defaults to 0.15."),
            endHooks: z.boolean().optional().describe("beam bars hooked at the ends (top down, bottom up). Defaults to true."),
            bottomX: barSpacing.optional().describe("footing mat along the footing; bottomY across. Spacing defaults to 0.15."),
            bottomY: barSpacing.optional(),
            hookUp: z.number().optional().describe("footing bar legs up, metres. Defaults to 0.15."),
            transverse: barSpacing.extend({ hookUp: z.number().optional() }).optional().describe("stripFooting U bars."),
            sheet,
            layers: z.number().int().min(1).max(2).optional().describe("wallMesh/slabMesh: 1 or 2 layers."),
            location: z.enum(["top", "bottom"]).optional().describe("slabMesh single layer. Defaults to top."),
            majorDirection: z.enum(["along", "vertical", "x", "y"]).optional(),
            lap: z.number().optional().describe("mesh sheet lap. Defaults to 0.20."),
          })
        )
        .min(1),
      ...selection,
      ...rebarCatalog,
      ...rebarPlacement,
      ...writeOptions,
    },
    async (args) => callRevit("rebar_by_type", args, ((args.timeoutSeconds ?? 1800) + 60) * 1000)
  );
}
