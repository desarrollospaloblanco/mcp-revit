import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { rebarCatalog } from "../utils/rebarOptions.js";

export function registerRebarTakeoffTool(server: McpServer) {
  server.tool(
    "rebar_takeoff",
    "Steel take-off of the rebar and fabric areas in quintals of 100 lb. Weighs every set from Revit's bar lengths, splits it " +
      "by a dividing line in plan (e.g. the party wall of a duplex; bars on the shared band count half for each side) and by level " +
      "band (below the first level or in a foundation = foundationName), gives identical bars the same piece mark, and counts the " +
      "commercial bars per size by first-fit cutting of stockLength (6, 9 or 12 m). Writes 'Peso qq', 'Peso qq <side>', " +
      "'Peso qq <level>', 'Largo de corte m' and 'Marca pieza'; builds the schedules ACERO 01-05 (by size, by level and element, " +
      "piece list, laps, mesh) and the 3D views 'ACERO - 3D completo' and 'ACERO - Zonas de traslape'; exports JSON and CSV. " +
      "Totals come exact and rounded up to the next whole quintal (each from its exact value), net and with waste.",
    {
      split: z
        .object({
          line: z.array(point2).length(2).describe("Two plan points of the dividing line, metres."),
          left: z.string().optional().describe("Name of the side to the left of line[0]->line[1]. Defaults to 'casa 1'."),
          right: z.string().optional().describe("Defaults to 'casa 2'."),
          sharedBand: z.number().optional().describe("Width of the shared band on the line, metres. Defaults to 0.24."),
        })
        .optional(),
      levels: z.array(z.string()).optional().describe("Levels that start a band; all levels by default."),
      foundationName: z.string().optional().describe("Band below the first level and foundation hosts. Defaults to 'Cimentación'."),
      waste: z.number().optional().describe("Waste as a fraction (0.03) or percent (3). Defaults to 0.03."),
      meshWeight: z
        .enum(["area", "wires"])
        .optional()
        .describe("area: net area x sheet laps x kg/m² (default); wires: the wires Revit actually laid out."),
      writeParameters: z.boolean().optional().describe("Defaults to true."),
      schedules: z.boolean().optional().describe("Create the ACERO schedules. Defaults to true."),
      views: z.boolean().optional().describe("Create the two ACERO 3D views. Defaults to true."),
      replaceSchedulesAndViews: z.boolean().optional().describe("Rebuild them when they exist. Defaults to false (kept)."),
      schedulePrefix: z.string().optional().describe("Defaults to 'ACERO'."),
      exportPath: z.string().optional().describe("JSON file or folder; the CSV goes next to it. Defaults to %APPDATA%\\RevitMCP\\takeoffs."),
      filter: z.object({ runMark: z.string().optional() }).optional().describe("Only steel placed by a run with this mark."),
      timeoutSeconds: z.number().optional(),
      stockLength: rebarCatalog.stockLength,
      lapFactor: rebarCatalog.lapFactor,
      lapLengths: rebarCatalog.lapLengths,
      ...writeOptions,
    },
    async (args) => callRevit("rebar_takeoff", args, ((args.timeoutSeconds ?? 1200) + 60) * 1000)
  );
}
