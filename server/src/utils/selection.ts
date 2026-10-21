import { z } from "zod";
import { point2 } from "./revitTool.js";

/** The element selection the modelling tools share: ids, or a filter by category, level, type, region and run mark. */
export const selection = {
  elementIds: z.array(z.number()).optional().describe("Elements to act on; when given, filter is ignored."),
  filter: z
    .object({
      categories: z.array(z.string()).optional().describe("BuiltInCategory names, e.g. OST_Walls."),
      level: z.string().optional().describe("Only elements based on this level."),
      typeContains: z.string().optional().describe("Only types whose name contains this text."),
      region: z.object({ min: point2, max: point2 }).optional().describe("Only elements whose box touches this plan rectangle."),
      runMark: z.string().optional().describe("Only elements created by a run with this mark."),
    })
    .optional(),
};
