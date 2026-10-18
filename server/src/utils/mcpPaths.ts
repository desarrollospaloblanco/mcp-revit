import os from "os";
import path from "path";

// Folders shared with the Revit plugin (%APPDATA%\RevitMCP): run records, captures and the
// registry of running Revit sessions. Reading them directly works even while Revit is busy.
const appData = process.env.APPDATA ?? path.join(os.homedir(), "AppData", "Roaming");

export const mcpRoot = path.join(appData, "RevitMCP");
export const runsDir = path.join(mcpRoot, "runs");
export const capturesDir = path.join(mcpRoot, "captures");
export const instancesDir = path.join(mcpRoot, "instances");
