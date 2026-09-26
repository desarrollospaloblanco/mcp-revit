import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const xy = z.array(z.number()).length(2).describe("[x, y] in metres from the internal origin");

const placed = {
  id: z.string().describe("Stable id; the builder matches it to the element built from it before"),
  type: z.string().describe("Type name, as defined under types or already present in the document"),
  repeatOn: z
    .array(z.string())
    .optional()
    .describe(
      "Extra levels to build a copy on. Each copy takes that level as its base level and gets the id '<id>@<level>'."
    ),
};

const layeredType = z.object({
  name: z.string(),
  base: z.string().optional().describe("Existing type to duplicate. Defaults to the first basic one."),
  thickness: z.number().describe("Total thickness in metres; the type is made a single structural layer"),
  material: z.string().optional().describe("Material name for that layer. Defaults to the base type's structural material."),
});

const sectionType = z.object({
  name: z.string(),
  family: z.string().optional().describe("Loaded family to use. Defaults to the first with a b × h section."),
  b: z.number().describe("Section width in metres"),
  h: z.number().describe("Section depth in metres"),
  widthParam: z.string().optional().describe("Type parameter for b, when not one of b/Width/Anchura/Ancho/Base"),
  depthParam: z.string().optional().describe("Type parameter for h, when not one of h/Depth/Profundidad/Peralte/Altura/Height"),
  architectural: z.boolean().optional().describe("Columns only: architectural instead of structural"),
});

const openingType = z.object({
  name: z.string(),
  family: z.string().optional().describe("Loaded family to duplicate from; required when the type does not exist"),
  width: z.number().describe("Metres"),
  height: z.number().describe("Metres"),
});

const opening = z.object({
  ...placed,
  hostWall: z
    .string()
    .describe("Spec id of the host wall. Copies made by repeatOn host in '<hostWall>@<level>'."),
  at: xy.describe("Centre of the opening in plan; projected onto the host wall"),
  level: z.string().optional().describe("Defaults to the host wall's base level"),
  sill: z.number().optional().describe("Sill height above the level, metres. 0 for doors."),
  facing: xy.optional().describe("Plan direction the opening should face (a door's swing side)"),
  hand: xy.optional().describe("Plan direction from hinge towards latch"),
});

const specSchema = z.object({
  levels: z
    .array(
      z.object({
        id: z.string(),
        name: z.string().optional().describe("Revit name. Defaults to the id; an existing untagged level with it is adopted."),
        elevation: z.number().describe("Metres"),
        createPlan: z.boolean().optional().describe("Create a floor plan if it has none. Defaults to true."),
      })
    )
    .optional(),
  grids: z
    .array(
      z.object({
        id: z.string(),
        name: z.string().optional(),
        start: xy,
        end: xy,
        mid: xy.optional().describe("Point on the arc, for a curved grid"),
      })
    )
    .optional(),
  types: z
    .object({
      walls: z.array(layeredType).optional(),
      floors: z.array(layeredType).optional(),
      columns: z.array(sectionType).optional(),
      beams: z.array(sectionType).optional(),
      doors: z.array(openingType).optional(),
      windows: z.array(openingType).optional(),
    })
    .optional(),
  columns: z
    .array(
      z.object({
        ...placed,
        at: xy,
        baseLevel: z.string(),
        topLevel: z.string().describe("Level id or name, or '+1' for the next spec level above the base"),
        baseOffset: z.number().optional(),
        topOffset: z.number().optional(),
        rotation: z.number().optional().describe("Degrees counter-clockwise"),
      })
    )
    .optional(),
  walls: z
    .array(
      z.object({
        ...placed,
        start: xy,
        end: xy,
        mid: xy.optional().describe("Point on the arc, for a curved wall"),
        baseLevel: z.string(),
        topLevel: z.string().optional().describe("Level id or name, or '+1'. Either this or height."),
        height: z.number().optional().describe("Unconnected height in metres, when there is no topLevel"),
        baseOffset: z.number().optional(),
        topOffset: z.number().optional(),
        structural: z.boolean().optional(),
        alignment: z
          .enum(["center", "left", "right"])
          .optional()
          .describe("What start → end traces: the centreline (default) or the left/right face looking along it"),
      })
    )
    .optional(),
  beams: z
    .array(
      z.object({
        ...placed,
        start: xy,
        end: xy,
        mid: xy.optional(),
        level: z.string(),
        offset: z.number().optional().describe("Top of beam relative to the level, metres. 0 is flush."),
        endOffset: z.number().optional().describe("Top of beam at its end, for a sloped beam following a ramp"),
      })
    )
    .optional(),
  floors: z
    .array(
      z.object({
        ...placed,
        level: z.string(),
        offset: z.number().optional().describe("Top of slab relative to the level, metres"),
        boundary: z.array(xy).min(3).describe("Outline vertices; the closing edge is implied"),
        holes: z.array(z.array(xy).min(3)).optional().describe("Openings inside the outline"),
        structural: z.boolean().optional().describe("Defaults to true"),
        slopeArrow: z
          .object({ from: xy, to: xy, percent: z.number() })
          .optional()
          .describe("For a ramp: the slab is at level + offset at 'from' and rises towards 'to'"),
      })
    )
    .optional(),
  doors: z.array(opening).optional(),
  windows: z.array(opening).optional().describe("Windows, and sliding doors that are window families"),
});

export function registerBuildModelFromSpecTool(server: McpServer) {
  server.tool(
    "build_model_from_spec",
    "Build a model from a spec in metres: levels (with floor plans), grids, columns, walls, doors and " +
      "windows, beams and floors, in that order, each stage in its own transaction and the whole build as one undo step. " +
      "Idempotent: every element is stamped with its spec id, so re-sending a corrected spec updates " +
      "what changed, skips what did not, and never duplicates. Types missing from the document are " +
      "created from types.* by duplicating a base type; existing types the spec did not create are " +
      "never modified. Revit warnings are dismissed and reported; an element Revit refuses is deleted " +
      "and reported by its spec id instead of sinking the stage. Use dryRun first to validate against " +
      "the real document without keeping anything. For large buildings pass specPath instead of spec.",
    {
      spec: specSchema.optional().describe("The spec inline. Use specPath for large buildings."),
      specPath: z.string().optional().describe("Absolute path to a JSON file with the spec, read by Revit."),
      specName: z
        .string()
        .optional()
        .describe("Name that scopes the stamps, so two specs in one model never touch each other. Defaults to 'spec'."),
      documentTitle: z.string().optional().describe("Exact title of the open document to build into. Omit for the active one."),
      stages: z
        .array(z.enum(["levels", "grids", "columns", "walls", "openings", "beams", "floors"]))
        .optional()
        .describe("Only run these stages. Defaults to all."),
      dryRun: z.boolean().optional().describe("Build everything, report, then roll it all back."),
      deleteMissing: z
        .boolean()
        .optional()
        .describe(
          "Delete elements this spec built earlier that are no longer in it, for the stages run. Levels are never deleted. Defaults to false (they are only reported)."
        ),
      reportPath: z.string().optional().describe("Write the full report, with every spec id → element id, to this JSON file."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("build_model_from_spec", args, 600000);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `build model from spec failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
