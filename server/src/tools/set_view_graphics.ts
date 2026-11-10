import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, documentOption, point2 } from "../utils/revitTool.js";

const color = z
  .union([z.array(z.number().int().min(0).max(255)).length(3), z.string().regex(/^#?[0-9a-fA-F]{6}$/)])
  .describe("[r, g, b] or '#RRGGBB'.");

const looks = {
  surfaceColor: color.optional().describe("Solid surface fill (projection) in this color."),
  cutColor: color.optional().describe("Solid cut fill in this color."),
  cutLineColor: color.optional().describe("Cut line color."),
  lineColor: color.optional().describe("Projection line color."),
  transparency: z.number().int().min(0).max(100).optional().describe("Surface transparency, 0 to 100."),
  halftone: z.boolean().optional(),
};

const category = z.string().describe("BuiltInCategory name, e.g. OST_Walls (the OST_ prefix is optional).");

export function registerSetViewGraphicsTool(server: McpServer) {
  server.tool(
    "set_view_graphics",
    "Presentation graphics on existing views, in one or more open documents (one transaction each). cut: solid cut " +
      "fill (black by default) for categories, e.g. everything cut in a section. categories: per category overrides, " +
      "merged onto what the view already has unless reset. rules: element overrides; each element of the rule " +
      "categories visible in the view takes the first rule that matches (category, type name, base level elevation " +
      "in metres with min inclusive and max exclusive, plan region tested at the location midpoint or bbox centre, " +
      "curtainWall); a rule without overrides is an exclusion. resetElements (default true) first clears element " +
      "overrides of the rule and hosted categories so a re-run does not keep old colours. hostedFollowHost: doors and " +
      "windows whose host wall took one of the given rules get their own look. hideCategories, displayStyle, " +
      "detailLevel. copyFrom: copy category overrides (common model categories by default) and element overrides " +
      "from another view of the same document; applied first, so cut/categories/rules win over it. View templates are " +
      "refused; a view whose template controls V/G gets a warning (element overrides still show, category ones do " +
      "not). dryRun counts per view and rule, then rolls back. Metres everywhere.",
    {
      views: z.array(z.string()).min(1).describe("View names or ids; the same names are looked up in every document."),
      documents: z
        .array(z.string())
        .optional()
        .describe("Titles of the open documents to dress with the same settings; defaults to documentTitle or the active one."),
      cut: z
        .object({
          categories: z.array(category).min(1),
          color: color.optional().describe("Cut fill color. Defaults to black."),
          lineColor: color.optional().describe("Cut line color. Defaults to the fill color."),
        })
        .optional(),
      categories: z
        .array(
          z.object({
            category,
            reset: z.boolean().optional().describe("Start from no override instead of the view's current one."),
            ...looks,
          })
        )
        .optional(),
      rules: z
        .array(
          z.object({
            name: z.string().optional().describe("Defaults to 'rule N' (1-based); hostedFollowHost refers to it."),
            match: z.object({
              categories: z.array(category).min(1),
              typeStartsWith: z.string().optional().describe("Type name prefix, case-insensitive."),
              typeContains: z.string().optional().describe("Text in the type name, case-insensitive."),
              levelElevation: z
                .object({ min: z.number().optional(), max: z.number().optional() })
                .optional()
                .describe("Elevation range in metres of the element's base level: min <= z < max."),
              region: z.object({ min: point2, max: point2 }).optional().describe("Plan rectangle in metres."),
              curtainWall: z.boolean().optional().describe("true: only curtain walls; false: skip curtain walls."),
            }),
            ...looks,
          })
        )
        .optional(),
      resetElements: z.boolean().optional().describe("Clear element overrides of the rule and hosted categories first. Defaults to true."),
      hostedFollowHost: z
        .array(
          z.object({
            hostRules: z.array(z.string()).min(1).describe("Rule names; an insert whose host took one of them gets this look."),
            categories: z.array(category).optional().describe("Defaults to OST_Windows and OST_Doors."),
            ...looks,
          })
        )
        .optional(),
      hideCategories: z.array(category).optional().describe("Categories the view cannot hide are reported as skipped."),
      displayStyle: z.enum(["wireframe", "hidden", "shading", "shadingWithEdges", "consistent", "realistic"]).optional(),
      detailLevel: z.enum(["coarse", "medium", "fine"]).optional(),
      copyFrom: z
        .object({
          view: z.string().describe("Source view name or id in the same document."),
          categories: z.array(category).optional().describe("Defaults to the common model categories."),
          elementOverrides: z.boolean().optional().describe("Also copy element overrides of those categories. Defaults to true."),
        })
        .optional(),
      dryRun: z.boolean().optional(),
      ...documentOption,
      swallowWarnings: z
        .boolean()
        .optional()
        .describe("Delete Revit warnings instead of letting them stop the call with a dialog. Defaults to true."),
    },
    async (args) => callRevit("set_view_graphics", args, 620000)
  );
}
