// Runs a C# snippet from a file in Revit through send_code_to_revit, with transactionMode "none"
// so the snippet opens its own transactions on the document it picks by title.
//
//   node scripts/run-code.mjs snippet.cs [timeoutSeconds] [--dry-run] [--no-group] [--title "text"]
//
// The snippet is the body of `object Execute(Document document, object[] parameters)`: find the
// target document by title in document.Application.Documents (never rely on the active one) and
// return a string.
//
// Each run is one undo step and is recorded under %APPDATA%\RevitMCP\runs. --dry-run reports the
// changes and rolls them back. --no-group is required for snippets that save, synchronize, open or
// close documents. The Revit session is the only running one, the one in REVIT_MCP_PORT, or 8080.
import { readFileSync } from "fs";
import { fileURLToPath, pathToFileURL } from "url";
import { basename, dirname, join } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const build = (name) => pathToFileURL(join(here, "../server/build/utils", name)).href;
const { RevitClientConnection } = await import(build("SocketClient.js"));
const { resolvePort } = await import(build("RevitInstances.js"));

const args = process.argv.slice(2);
const flag = (name) => args.includes(name);
const option = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const positional = args.filter((a, i) => !a.startsWith("--") && args[i - 1] !== "--title");
const [file, timeout = "120"] = positional;
if (!file) {
  console.error('usage: node scripts/run-code.mjs snippet.cs [timeoutSeconds] [--dry-run] [--no-group] [--title "text"]');
  process.exit(2);
}

const port = resolvePort();
const client = new RevitClientConnection("127.0.0.1", port);
await new Promise((resolve, reject) => {
  client.socket.once("connect", resolve);
  client.socket.once("error", () => reject(new Error(`Revit is not listening on port ${port}: switch the MCP service on`)));
  client.connect();
});

try {
  const seconds = Number(timeout);
  const result = await client.sendCommand(
    "send_code_to_revit",
    {
      code: readFileSync(file, "utf8"),
      parameters: [],
      transactionMode: "none",
      dryRun: flag("--dry-run"),
      group: !flag("--no-group"),
      title: option("--title") ?? basename(file),
      timeoutSeconds: Math.max(5, Math.min(3600, seconds)),
    },
    (seconds + 15) * 1000
  );
  console.log(typeof result === "string" ? result : JSON.stringify(result, null, 2));
} catch (error) {
  console.error("ERROR:", error.message);
  process.exitCode = 1;
} finally {
  client.disconnect();
}
