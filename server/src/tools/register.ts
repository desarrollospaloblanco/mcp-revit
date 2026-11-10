import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { z } from "zod";

/**
 * Tools declare their parameters as a raw shape, which zod turns into an object that silently drops
 * unknown keys. A misspelt option then falls back to its default: save_documents called with
 * documentTitles instead of documents went over every open model. Registering the shape as a strict
 * object makes a wrong parameter name fail with its name instead.
 */
function rejectUnknownParameters(server: McpServer) {
  const original = server.tool.bind(server) as (...args: unknown[]) => unknown;
  const isShape = (value: unknown) =>
    typeof value === "object" &&
    value !== null &&
    !(value instanceof z.ZodType) &&
    Object.values(value).some((v) => v instanceof z.ZodType);
  (server as unknown as { tool: (...args: unknown[]) => unknown }).tool = (...args: unknown[]) => {
    const [name, description, shape, callback] = args;
    if (args.length === 4 && typeof name === "string" && typeof description === "string" && isShape(shape) && typeof callback === "function") {
      return server.registerTool(
        name,
        { description, inputSchema: z.object(shape as z.ZodRawShape).strict() },
        callback as Parameters<McpServer["registerTool"]>[2]
      );
    }
    return original(...args);
  };
}

export async function registerTools(server: McpServer) {
  rejectUnknownParameters(server);

  // 获取当前文件的目录路径
  const __filename = fileURLToPath(import.meta.url);
  const __dirname = path.dirname(__filename);

  // 读取tools目录下的所有文件
  const files = fs.readdirSync(__dirname);

  // 过滤出.ts或.js文件，但排除index文件和register文件
  const toolFiles = files.filter(
    (file) =>
      (file.endsWith(".ts") || file.endsWith(".js")) &&
      file !== "index.ts" &&
      file !== "index.js" &&
      file !== "register.ts" &&
      file !== "register.js"
  );

  // 动态导入并注册每个工具
  for (const file of toolFiles) {
    try {
      // 构建导入路径
      const importPath = `./${file.replace(/\.(ts|js)$/, ".js")}`;

      // 动态导入模块
      const module = await import(importPath);

      // 查找并执行注册函数
      const registerFunctionName = Object.keys(module).find(
        (key) => key.startsWith("register") && typeof module[key] === "function"
      );

      if (registerFunctionName) {
        module[registerFunctionName](server);
        console.error(`已注册工具: ${file}`);
      } else {
        console.warn(`警告: 在文件 ${file} 中未找到注册函数`);
      }
    } catch (error) {
      console.error(`注册工具 ${file} 时出错:`, error);
    }
  }
}
