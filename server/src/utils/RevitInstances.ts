import fs from "fs";
import path from "path";
import { instancesDir } from "./mcpPaths.js";

export interface RevitInstance {
  pid: number;
  port: number;
  revitVersion: string;
  startedAt: string;
  updatedAt: string;
  documents: string[];
}

export const DEFAULT_PORT = 8080;

// Port chosen with select_revit_instance for the rest of this server session.
let selectedPort: number | undefined;

function isAlive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    // EPERM means the process exists but belongs to another user: still alive.
    return (error as NodeJS.ErrnoException).code === "EPERM";
  }
}

/** Revit sessions with the MCP service on, read from the registry the plugin writes. Stale files are removed. */
export function listRevitInstances(): RevitInstance[] {
  if (!fs.existsSync(instancesDir)) return [];
  const instances: RevitInstance[] = [];
  for (const file of fs.readdirSync(instancesDir).filter((f) => f.endsWith(".json"))) {
    const full = path.join(instancesDir, file);
    try {
      const info = JSON.parse(fs.readFileSync(full, "utf8")) as RevitInstance;
      if (isAlive(info.pid)) instances.push(info);
      else fs.rmSync(full, { force: true });
    } catch {
      // A file being rewritten by the plugin can be momentarily incomplete; skip it this time.
    }
  }
  return instances.sort((a, b) => a.port - b.port);
}

export function selectPort(port: number | undefined): void {
  selectedPort = port;
}

/**
 * Port to connect to: REVIT_MCP_PORT, then the session chosen with select_revit_instance, then the
 * only running session, then 8080.
 */
export function resolvePort(): number {
  const fromEnv = Number(process.env.REVIT_MCP_PORT);
  if (Number.isInteger(fromEnv) && fromEnv > 0) return fromEnv;
  if (selectedPort !== undefined) return selectedPort;
  const running = listRevitInstances();
  if (running.length === 1) return running[0].port;
  return DEFAULT_PORT;
}
