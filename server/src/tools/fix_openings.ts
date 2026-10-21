import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { callRevit, point2, writeOptions } from "../utils/revitTool.js";
import { selection } from "../utils/selection.js";

export function registerFixOpeningsTool(server: McpServer) {
  server.tool(
    "fix_openings",
    "Fix doors and windows (selection defaults to both categories; use check_hosted_openings to find them). " +
      "flipHand / flipFacing: flip. facePoint: make the door face a point (the room it opens into), flipping only " +
      "the ones facing away. jamb: slide along the wall so the near side sits jambMeters from the nearest crossing " +
      "wall face. mirror: mirror in place across x = value or y = value. rehost: recreate on another wall at the " +
      "same point (hostId, or the other wall passing through the opening, e.g. a duplicated wall), keeping type, " +
      "flips and parameters; the new id is reported. dryRun rolls back. Check the plan logic (swing side, rooms " +
      "joined) with the user.",
    {
      operation: z.enum(["flipHand", "flipFacing", "facePoint", "jamb", "mirror", "rehost"]),
      ...selection,
      point: point2.optional().describe("facePoint: the point the door must face."),
      jambMeters: z.number().min(0).optional().describe("jamb: distance to the wall face. Defaults to 0.10."),
      searchMeters: z.number().min(0).optional().describe("jamb: how far to look for the wall. Defaults to 0.6."),
      plane: z.object({ axis: z.enum(["x", "y"]), value: z.number() }).optional().describe("mirror: the plane."),
      hostId: z.number().optional().describe("rehost: the new host wall."),
      dryRun: z.boolean().optional(),
      ...writeOptions,
    },
    async (args) => callRevit("fix_openings", args, 920000)
  );
}
