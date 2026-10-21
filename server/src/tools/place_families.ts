import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";

export function registerPlaceFamiliesTool(server: McpServer) {
  server.tool(
    "place_families",
    "Place many instances of one family type in a call: load the .rfa first when familyPath is given, then place " +
      "at points in metres with level, offset above the level, rotation, in-place mirror and instance parameters " +
      "(lengths in metres), hosted on a wall with hostId; or onTopOf places one instance on top of each selected " +
      "element (e.g. a tree in every planter). The offset is written to the instance's offset parameter, which avoids " +
      "the level elevation being counted twice. With runMark a re-run replaces the previous run's instances.",
    {
      type: z.string().describe("'Family : Type' (or the type name when unique)."),
      familyPath: z.string().optional().describe("A .rfa to load (or reload) first."),
      instances: z
        .array(
          z.object({
            point: point2.describe("[x, y] or [x, y, z] in metres (z only without level)."),
            level: z.string().optional(),
            offsetMeters: z.number().optional(),
            rotationDeg: z.number().optional(),
            mirror: z.enum(["x", "y"]).optional().describe("Mirror in place across a plane normal to x or y."),
            hostId: z.number().optional().describe("Wall (or other host) id for hosted families."),
            parameters: z.record(z.union([z.string(), z.number(), z.boolean()])).optional(),
          })
        )
        .optional(),
      level: z.string().optional().describe("Default level for instances."),
      offsetMeters: z.number().optional().describe("Default offset."),
      rotationDeg: z.number().optional().describe("Default rotation."),
      onTopOf: z
        .object({ ...selection, extraOffsetMeters: z.number().optional() })
        .optional()
        .describe("One instance on top of each selected element, at its box centre."),
      replacePrevious: z.boolean().optional().describe("With runMark, delete the previous run first. Defaults to true."),
      ...writeOptions,
    },
    async (args) => callRevit("place_families", args, 1820000)
  );
}
