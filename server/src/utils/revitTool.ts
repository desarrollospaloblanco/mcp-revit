import { z } from "zod";
import { withRevitConnection } from "./ConnectionManager.js";

/** Options shared by the tools built on the common base: target document, warnings and run mark. */
export const documentOption = {
  documentTitle: z.string().optional().describe("Title of the open document to act on; defaults to the active one."),
};

export const writeOptions = {
  ...documentOption,
  runMark: z
    .string()
    .optional()
    .describe("Label stored (hidden) on what this call creates, so run_marks can count, select or delete it later."),
  swallowWarnings: z
    .boolean()
    .optional()
    .describe("Delete Revit warnings instead of letting them stop the call with a dialog. Defaults to true; they come back in 'warnings'."),
};

export const point2 = z.array(z.number()).min(2).max(3).describe("[x, y] in metres");
export const point3 = z.array(z.number()).length(3).describe("[x, y, z] in metres");

/** Sends a command to Revit and wraps the JSON answer, or the error, as MCP text content. */
export async function callRevit(command: string, args: unknown, timeoutMs: number) {
  try {
    const response = await withRevitConnection(async (revitClient) => {
      return await revitClient.sendCommand(command, args, timeoutMs);
    });
    return { content: [{ type: "text" as const, text: JSON.stringify(response, null, 2) }] };
  } catch (error) {
    return {
      content: [
        { type: "text" as const, text: `${command} failed: ${error instanceof Error ? error.message : String(error)}` },
      ],
    };
  }
}
