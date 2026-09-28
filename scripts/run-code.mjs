// Runs a C# snippet from a file in Revit through send_code_to_revit, with transactionMode "none"
// so the snippet opens its own transactions on the document it picks by title.
//
//   node scripts/run-code.mjs snippet.cs [timeoutSeconds]
//
// The snippet is the body of `object Execute(Document document, object[] parameters)`: find the
// target document by title in document.Application.Documents (never rely on the active one) and
// return a string.
import { readFileSync } from "fs";
import { fileURLToPath, pathToFileURL } from "url";
import { dirname, join } from "path";

const here = dirname(fileURLToPath(import.meta.url));
const { RevitClientConnection } = await import(pathToFileURL(join(here, "../server/build/utils/SocketClient.js")).href);

const [file, timeout = "120"] = process.argv.slice(2);
if (!file) {
  console.error("usage: node scripts/run-code.mjs snippet.cs [timeoutSeconds]");
  process.exit(2);
}

const client = new RevitClientConnection("localhost", 8080);
await new Promise((resolve, reject) => {
  client.socket.once("connect", resolve);
  client.socket.once("error", () => reject(new Error("Revit is not listening on port 8080: switch the MCP service on")));
  client.connect();
});

try {
  const result = await client.sendCommand(
    "send_code_to_revit",
    { code: readFileSync(file, "utf8"), parameters: [], transactionMode: "none" },
    Number(timeout) * 1000
  );
  console.log(typeof result === "string" ? result : JSON.stringify(result, null, 2));
} catch (error) {
  console.error("ERROR:", error.message);
  process.exitCode = 1;
} finally {
  client.disconnect();
}
