import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerDuplicateSchedulesWithParameterTool(server: McpServer) {
  server.tool(
    "duplicate_schedules_with_parameter",
    "Duplicate schedules for another value of a parameter: renames the copies with a prefix, retargets the parameter filter to the new value and writes the new value into the copies' own view parameter, so they group next to the originals in the project browser.",
    {
      scheduleIds: z
        .array(z.number())
        .optional()
        .describe(
          "Element ids of the schedules to duplicate. When omitted, the schedules currently selected in the project browser are used"
        ),
      parameterName: z
        .string()
        .describe("Name of the parameter that drives both the filter and the view grouping (for example 'Ubicación')"),
      newValue: z
        .string()
        .describe("Value the copies should carry, in the filter and in the view parameter (for example 'QT CENTRO')"),
      namePrefix: z
        .string()
        .optional()
        .default("")
        .describe("Prefix for the new names, for example 'QC - '"),
      prefixToReplace: z
        .string()
        .optional()
        .describe("Prefix stripped from the original name before applying namePrefix, for example 'Q - '. Omit to keep the whole original name"),
      fieldType: z
        .enum(["Instance", "ElementType", "Material", "Room", "Space", "ProjectInfo"])
        .optional()
        .default("Instance")
        .describe("Which flavour of the parameter the filter uses"),
      updateFilter: z
        .boolean()
        .optional()
        .default(true)
        .describe("Retarget the copy's filter on this parameter to newValue"),
      addFilterIfMissing: z
        .boolean()
        .optional()
        .default(false)
        .describe("Also add the filter when the copy has none for this parameter. Off by default, so schedules without the filter are reported and left alone"),
      setViewParameter: z
        .boolean()
        .optional()
        .default(true)
        .describe("Write newValue into the copy's own view parameter, which is what groups it in the project browser"),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("duplicate_schedules_with_parameter", args);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Schedule duplication failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
