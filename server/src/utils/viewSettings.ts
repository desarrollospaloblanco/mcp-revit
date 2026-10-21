import { z } from "zod";

/** View settings shared by the view-creating tools. */
export const viewSettings = {
  template: z.string().optional().describe("View template name, or 'none' to remove it. Settings the template controls cannot change."),
  scale: z.number().int().min(1).max(10000).optional().describe("Scale denominator, e.g. 100."),
  detailLevel: z.enum(["coarse", "medium", "fine"]).optional(),
  displayStyle: z.enum(["realistic", "shaded", "hidden", "consistent", "wireframe"]).optional(),
  hideCategories: z.array(z.string()).optional().describe("BuiltInCategory names to hide, e.g. OST_Furniture, OST_Grids."),
  hideImports: z.boolean().optional().describe("Hide imported CAD. Defaults to true."),
};
