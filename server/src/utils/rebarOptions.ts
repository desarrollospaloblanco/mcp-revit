import { z } from "zod";

/** Options shared by place_rebar and rebar_by_type: commercial length, laps, hooks and placement switches. */
export const rebarCatalog = {
  stockLength: z
    .union([z.literal(6), z.literal(9), z.literal(12)])
    .optional()
    .describe("Commercial bar length in metres: 6, 9 or 12. Defaults to 9. Longer bars are cut into pieces joined by laps."),
  lapFactor: z
    .number()
    .optional()
    .describe("Lap splice length in bar diameters, rounded to 0.05 m. Defaults to 40 (#3 0.40, #4 0.50, #5 0.65, #6 0.75, ø7.2 0.30)."),
  lapLengths: z
    .record(z.number())
    .optional()
    .describe("Lap length in metres per size, overriding lapFactor, e.g. {\"#4\": 0.60}."),
  hookLengths: z
    .record(z.number())
    .optional()
    .describe("90° hook leg in metres per size (defaults #3 0.15, #4 0.20, #5 0.25, #6 0.30)."),
};

export const rebarPlacement = {
  autoSpliceZones: z
    .boolean()
    .optional()
    .describe(
      "When a bar has no spliceZones, find the joints its longest segment crosses (columns, walls and beams across a horizontal bar; " +
        "slabs and beams across a vertical one) and splice only in the middle of the clear spans, 2h away from beam supports. Defaults to true."
    ),
  lapMarkers: z
    .boolean()
    .optional()
    .describe("Draw a magenta rectangle on every splice, grouped as 'Traslape T001 - #4 0.50 m - <element>'. Defaults to true."),
  dryRun: z.boolean().optional().describe("Build everything and roll it back, to see the pieces, laps and failures first."),
  replacePrevious: z
    .boolean()
    .optional()
    .describe("With runMark, delete the steel and lap markers of the previous run with that mark first. Defaults to true."),
  verifyInsideConcrete: z
    .boolean()
    .optional()
    .describe(
      "Intersect every bar (all bars of each set, hook legs drawn as segments included) with the solids of the walls, slabs, beams, " +
        "columns, foundations and stairs, and list in insideConcrete the sets with more than 5 % of their length outside the concrete. " +
        "Hooks given as hook types and welded mesh are not measured. Defaults to true."
    ),
  timeoutSeconds: z.number().optional().describe("Defaults to 1800; large houses take several minutes."),
};
