import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point3, writeOptions } from "../utils/revitTool.js";
import { rebarCatalog, rebarPlacement } from "../utils/rebarOptions.js";

const params = z
  .record(z.union([z.string(), z.number(), z.boolean()]))
  .optional()
  .describe("Extra instance parameters to write by name (lengths in metres).");

export function registerPlaceRebarTool(server: McpServer) {
  server.tool(
    "place_rebar",
    "Place reinforcement read from the drawings as native Revit rebar and fabric areas. Each bar set is the polyline of its " +
      "first bar in metres (hook legs as segments), repeated count times at spacing along normal. Bars longer than the " +
      "commercial length (stockLength 6, 9 or 12 m) are cut into pieces joined by laps of 40 db, with every splice inside the " +
      "allowed zones (spliceZones, or found from the joints the bar crosses) so no lap falls in a joint; successive sets of an " +
      "element splice from alternate ends. Hosts are found from the model when hostId is missing. Writes the 'MCP Acero' " +
      "parameters (element, group, size, description, lap ids) and draws a marker on every splice. Run rebar_takeoff afterwards " +
      "for weights, schedules, views and the export.",
    {
      bars: z
        .array(
          z.object({
            element: z.string().optional().describe("Element label, e.g. 'Viga V-3 eje 6'. Splices alternate per element."),
            group: z.string().optional().describe("Element group for the schedules, e.g. Columna, Viga, Muro, Losa, Zapata."),
            description: z.string().optional().describe("Bar description, e.g. '2#4 superior corrida (V-2)'."),
            bar: z.string().describe("Size: #3, No.4, ø7.2, 7.2mm, 12mm."),
            points: z.array(point3).min(2).describe("Polyline of the first bar in metres, hook legs included."),
            closed: z.boolean().optional().describe("Closed stirrup or tie (the loop is closed for you)."),
            style: z.enum(["standard", "stirrup"]).optional().describe("stirrup for ties, links and stirrups. Defaults to standard."),
            normal: point3.optional().describe("Normal of the bar plane and direction the set repeats in. Defaults to [0,0,1]."),
            count: z.number().int().optional(),
            spacing: z.number().optional().describe("Distance between bars in metres."),
            hostId: z.number().optional(),
            startHook: z.string().optional().describe("Rebar hook type name, when the hook is not drawn as a segment."),
            endHook: z.string().optional(),
            spliceZones: z
              .array(z.array(z.number()).length(2))
              .optional()
              .describe("Where splice centres may fall: [from, to] in metres along the longest segment from its first point."),
            parameters: params,
          })
        )
        .optional(),
      meshes: z
        .array(
          z.object({
            element: z.string().optional(),
            group: z.string().optional(),
            description: z.string().optional(),
            loops: z.array(z.array(point3).min(3)).min(1).describe("Outline then holes, in metres, on the host face."),
            location: z.enum(["top", "bottom", "exterior", "interior"]).optional().describe("Defaults to top/exterior."),
            majorDirection: point3.optional().describe("Direction of the sheet length. Defaults to [1,0,0]."),
            origin: point3.optional().describe("Sheet layout origin; the first outline point by default."),
            lap: z.number().optional().describe("Sheet lap in metres. Defaults to 0.20."),
            sheet: z
              .union([
                z.string(),
                z.object({
                  name: z.string(),
                  wireMm: z.number().optional(),
                  spacing: z.number().optional(),
                  width: z.number().optional(),
                  length: z.number().optional(),
                }),
              ])
              .optional()
              .describe("Sheet type name or definition. Defaults to 6x6-3/3 (6.19 mm @ 0.1524, 2.35 x 6.00 m)."),
            hostId: z.number().optional(),
            parameters: params,
          })
        )
        .optional(),
      autoHosts: z.boolean().optional().describe("Find the host of bars without hostId. Defaults to true."),
      ...rebarCatalog,
      ...rebarPlacement,
      ...writeOptions,
    },
    async (args) => callRevit("place_rebar", args, ((args.timeoutSeconds ?? 1800) + 60) * 1000)
  );
}
