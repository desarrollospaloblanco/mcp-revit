import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerSetScheduleParameterFilterTool(server: McpServer) {
  server.tool(
    "set_schedule_parameter_filter",
    "Add or update a parameter based filter on one or more schedules. The parameter field is added hidden when missing, so visible columns are untouched. Existing filters are kept unless replaceExistingFilters is true.",
    {
      scheduleIds: z
        .array(z.number())
        .optional()
        .describe(
          "Element ids of the schedules to filter. When omitted, the schedules currently selected in the project browser are used"
        ),
      parameterName: z
        .string()
        .describe("Name of the parameter to filter by, exactly as it appears in Revit (for example 'Ubicación')"),
      value: z
        .string()
        .describe("Value the filter compares against (for example 'QT')"),
      filterType: z
        .enum([
          "Equal",
          "NotEqual",
          "Contains",
          "NotContains",
          "BeginsWith",
          "NotBeginsWith",
          "EndsWith",
          "NotEndsWith",
          "GreaterThan",
          "GreaterThanOrEqual",
          "LessThan",
          "LessThanOrEqual",
          "HasParameter",
          "HasNoParameter",
        ])
        .optional()
        .default("Equal")
        .describe("Comparison used by the filter. Prefer Equal over Contains when values share a prefix, such as QT and QT CENTRO"),
      fieldType: z
        .enum(["Instance", "ElementType", "Material", "Room", "Space", "ProjectInfo"])
        .optional()
        .default("Instance")
        .describe("Which flavour of the parameter to schedule. Instance filters by the element's own value"),
      replaceExistingFilters: z
        .boolean()
        .optional()
        .default(false)
        .describe("Clear every existing filter before adding this one. Off by default so current filters survive"),
      hideField: z
        .boolean()
        .optional()
        .default(true)
        .describe("Hide the parameter column when it has to be added, so the schedule layout does not change"),
    },
    async (args, extra) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("set_schedule_parameter_filter", args);
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
              text: `Schedule filtering failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
