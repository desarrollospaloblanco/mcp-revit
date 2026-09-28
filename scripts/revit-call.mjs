// Sends one command straight to the Revit plugin's socket, the way the MCP server does, without
// going through an MCP client. Useful for long builds and for scripting.
//
//   node scripts/revit-call.mjs <command> '<json params>' [timeoutSeconds]
//   node scripts/revit-call.mjs build_model_from_spec '{"specPath":"C:/.../spec.json","dryRun":true}' 1800
//
// Needs the MCP server built (npm run build in server/) and the service switched on in Revit.
import { fileURLToPath, pathToFileURL } from "url";
import { dirname, join } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const { RevitClientConnection } = await import(pathToFileURL(join(here, "../server/build/utils/SocketClient.js")).href);

const [command, rawParams = "{}", timeout = "600"] = process.argv.slice(2);
if (!command) {
  console.error("usage: node scripts/revit-call.mjs <command> '<json params>' [timeoutSeconds]");
  process.exit(2);
}

const client = new RevitClientConnection("localhost", 8080);
await new Promise((resolve, reject) => {
  client.socket.once("connect", resolve);
  client.socket.once("error", () => reject(new Error("Revit is not listening on port 8080: switch the MCP service on")));
  client.connect();
});

try {
  const result = await client.sendCommand(command, JSON.parse(rawParams), Number(timeout) * 1000);
  console.log(JSON.stringify(result, null, 2));
} catch (error) {
  console.error("ERROR:", error.message);
  process.exitCode = 1;
} finally {
  client.disconnect();
}
