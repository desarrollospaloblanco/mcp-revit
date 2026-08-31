# Revit MCP - Features Faltantes

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extender el fork local de `mcp-server-for-revit` para implementar `modify_element`, `get_parameter`, `set_parameter` y soporte para Revit 2026, logrando un toolset completo y funcional sin depender de `send_code_to_revit` como workaround para operaciones de parámetros.

**Architecture:** El repositorio tiene dos componentes acoplados: un servidor MCP en TypeScript/Node.js (el paquete npm) y un add-in de Revit en C# (`RevitMCPCommandSet.dll`). Cada nueva herramienta requiere implementación en ambos lados: una clase C# que implementa `IRevitCommand` en el add-in, y un archivo `.ts` que registra el tool en el servidor MCP. La comunicación entre ambos lados es WebSocket en el puerto 8080. El ciclo de desarrollo para cada feature es: prototipo con `send_code_to_revit` → implementar C# → compilar → desplegar DLL → reiniciar Revit → verificar → iterar.

**Tech Stack:** TypeScript + Node.js (MCP server), C# / .NET Framework 4.8 (Revit add-in), MSBuild 2019, Revit API 2025 + 2026, zod (validación de esquemas), Newtonsoft.Json (serialización C#).

**Spec:** `docs/superpowers/plans/2026-08-31-revit-mcp-features.md` (este archivo)

## Global Constraints

- C# target framework: `net48` (requerido por Revit API)
- MSBuild en: `C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe`
- RevitAPI 2025: `C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll`
- RevitAPI 2026: `C:\Program Files\Autodesk\Revit 2026\RevitAPI.dll`
- SDK del add-in: `RevitMCPSDK.dll` (ya instalado en `Addins\2025\revit_mcp_plugin\`)
- Deploy DLL 2025: `C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Commands\RevitMCPCommandSet\2025\`
- Logs del add-in: `C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Logs\`
- Repositorio upstream: `https://github.com/mcp-servers-for-revit/mcp-servers-for-revit`
- Directorio de trabajo: `C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\`
- No mezclar cambios no relacionados en un mismo commit
- Verificar funcionamiento en Revit antes de marcar un task como completo

---

## Mapa de archivos

```
Revit-MCP/                              (directorio de trabajo)
├── docs/superpowers/plans/             (este archivo)
├── mcp-server/                         (clonado del repo - servidor TypeScript)
│   ├── src/
│   │   └── tools/
│   │       ├── modify_element.ts       MODIFICAR: implementar (stub vacío)
│   │       ├── get_parameter.ts        CREAR: nueva herramienta
│   │       └── set_parameter.ts        CREAR: nueva herramienta
│   ├── package.json
│   └── tsconfig.json
└── revit-plugin/                       (clonado del repo - add-in C#)
    └── RevitMCPCommandSet/
        ├── RevitMCPCommandSet.csproj   MODIFICAR: referencias para 2025 y 2026
        └── Commands/
            ├── ModifyElementCommand.cs MODIFICAR: implementar (stub vacío)
            ├── GetParameterCommand.cs  CREAR: nueva clase
            └── SetParameterCommand.cs  CREAR: nueva clase
```

**Nota:** La estructura exacta del repo se verifica en el Task 0. Los paths anteriores son aproximados basados en las convenciones del proyecto.

---

## Task 0: Clonar repo e inspeccionar estructura

**Files:**
- Crea: `Revit-MCP/` (contenido del repo)

**Interfaces:**
- Produce: estructura del repo conocida, entorno de build verificado, interfaz `IRevitCommand` documentada

- [ ] **Step 1: Clonar el fork en el directorio de trabajo**

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP"
git clone https://github.com/mcp-servers-for-revit/mcp-servers-for-revit .
```

- [ ] **Step 2: Verificar la estructura del repositorio**

```powershell
Get-ChildItem -Recurse -Depth 3 | Where-Object {$_.Extension -in @('.cs','.csproj','.sln','.ts','.json')} | Select-Object FullName | Format-Table -AutoSize
```

Anotar: ¿Dónde está el `.sln` o `.csproj` del CommandSet? ¿Cuál es la carpeta exacta del servidor TypeScript?

- [ ] **Step 3: Inspeccionar la interfaz IRevitCommand en el código fuente**

```powershell
Get-ChildItem -Recurse -Filter "*.cs" | ForEach-Object { Select-String -Path $_.FullName -Pattern "interface IRevitCommand|class.*IRevitCommand|public.*Execute|public.*CommandName" } | Select-Object -First 20
```

Anotar la firma exacta de `IRevitCommand` - especialmente la firma del método `Execute`: sus tipos de entrada y salida.

- [ ] **Step 4: Instalar dependencias del servidor TypeScript**

```powershell
# Ajustar ruta según la estructura descubierta en Step 2
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\mcp-server"  # O la ruta real
npm install
```

- [ ] **Step 5: Verificar build inicial del servidor TypeScript**

```powershell
npm run build
```

Esperado: BUILD SUCCESSFUL sin errores. Si falla, leer error y corregir dependencias.

- [ ] **Step 6: Verificar build inicial del add-in C#**

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
# Ajustar ruta según Step 2
& $msb "RevitMCPCommandSet.csproj" /p:Configuration=Release /t:Build
```

Esperado: Build succeeded. Si falla por referencias faltantes a `RevitAPI.dll`, verificar que el `.csproj` las referencia como HintPath desde `C:\Program Files\Autodesk\Revit 2025\`.

- [ ] **Step 7: Configurar Claude Code para usar el servidor local en lugar del npm**

Editar `~/.claude.json` - cambiar la entrada `mcp-server-for-revit`:

```json
"mcp-server-for-revit": {
  "type": "stdio",
  "command": "node",
  "args": ["C:/Users/jborrayo.DPB/BIM Tools/Revit-MCP/mcp-server/build/index.js"],
  "env": {}
}
```

(Ajustar path del `build/index.js` según la estructura real del repo.)

- [ ] **Step 8: Verificar conexión con el servidor local**

Reiniciar Claude Code. Llamar `say_hello` desde Claude. Esperado: `{"success": true}`.

- [ ] **Step 9: Commit**

```bash
git add .
git commit -m "chore: fork inicial - entorno configurado para desarrollo local"
```

---

## Task 1: Prototipo de modify_element vía send_code_to_revit

**Propósito:** Antes de implementar en C#, validar la lógica de la API de Revit usando `send_code_to_revit`. Esto nos da retroalimentación inmediata sin necesidad de compilar.

**Files:**
- No modifica archivos permanentes

**Interfaces:**
- Produce: C# verificado que funciona para cambiar parámetros, listo para portarse a `ModifyElementCommand.cs`

- [ ] **Step 1: Prototipar cambio de parámetro string**

Pedir a Claude que ejecute este código via `send_code_to_revit`:

```csharp
// Obtener el ID de un elemento seleccionado primero con get_selected_elements
// Luego ajustar elementId al ID real
var elementId = new ElementId(ELEMENT_ID_AQUI);
var element = doc.GetElement(elementId);
if (element == null) return "Error: elemento no encontrado";

var param = element.LookupParameter("Comments");
if (param == null) return "Error: parámetro 'Comments' no encontrado";
if (param.IsReadOnly) return "Error: parámetro es de solo lectura";

using (var tx = new Transaction(doc, "MCP Test"))
{
    tx.Start();
    param.Set("Modificado por Claude MCP");
    tx.Commit();
}
return $"OK: parámetro actualizado. StorageType={param.StorageType}";
```

- [ ] **Step 2: Verificar en Revit**

Seleccionar el elemento y verificar que el parámetro "Comments" cambió en las propiedades de Revit.

- [ ] **Step 3: Prototipar soporte multi-tipo**

```csharp
var elementId = new ElementId(ELEMENT_ID_AQUI);
var element = doc.GetElement(elementId);
var results = new System.Collections.Generic.List<string>();

foreach (Parameter p in element.Parameters)
{
    if (!p.IsReadOnly && p.HasValue)
        results.Add($"{p.Definition.Name} | {p.StorageType} | {p.AsValueString()}");
}
return string.Join("\n", results.Take(15));
```

Esto nos muestra qué parámetros editables tiene un elemento real. Verificar que `StorageType` puede ser `String`, `Integer`, `Double`, `ElementId`.

- [ ] **Step 4: Prototipar manejo de los 4 storage types**

```csharp
var elementId = new ElementId(ELEMENT_ID_AQUI);
var element = doc.GetElement(elementId);
var paramName = "NOMBRE_PARAMETRO";
string newValue = "VALOR_NUEVO";

var param = element.LookupParameter(paramName);
if (param == null || param.IsReadOnly) return $"Error: {paramName} no disponible";

using (var tx = new Transaction(doc, "MCP SetParam"))
{
    tx.Start();
    switch (param.StorageType)
    {
        case StorageType.String:
            param.Set(newValue);
            break;
        case StorageType.Integer:
            param.Set(int.Parse(newValue));
            break;
        case StorageType.Double:
            // Revit usa pies internamente - si el valor viene en mm, convertir
            param.Set(double.Parse(newValue));
            break;
        case StorageType.ElementId:
            param.Set(new ElementId(int.Parse(newValue)));
            break;
        default:
            return $"Error: StorageType {param.StorageType} no soportado";
    }
    tx.Commit();
}
return $"OK: {paramName} = {newValue}";
```

Esperado: el parámetro cambia en Revit. Documentar cualquier edge case encontrado (parámetros de sistema, unidades, etc.).

---

## Task 2: Implementar ModifyElementCommand en C#

**Files:**
- Modifica: `revit-plugin/RevitMCPCommandSet/Commands/ModifyElementCommand.cs` (o la ruta exacta del repo)

**Interfaces:**
- Consumes: firma exacta de `IRevitCommand` (descubierta en Task 0, Step 3)
- Produce: clase `ModifyElementCommand` que acepta `{elementId: int, changes: [{parameterName: string, value: string}]}`

- [ ] **Step 1: Leer la clase existente del stub**

```powershell
Get-ChildItem -Recurse -Filter "ModifyElementCommand.cs" | ForEach-Object { Get-Content $_.FullName }
```

Si el stub está vacío (`export {}`), crear desde cero siguiendo el patrón de otra clase implementada (ej: `DeleteElementCommand.cs`).

- [ ] **Step 2: Implementar ModifyElementCommand.cs**

Basado en la firma de `IRevitCommand` descubierta en Task 0 y el patrón de comandos existentes:

```csharp
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;  // ajustar namespace según repo

namespace RevitMCPCommandSet.Commands
{
    public class ModifyElementCommand : IRevitCommand
    {
        public string CommandName => "modify_element";

        public object Execute(JObject parameters, Document document)
        {
            var elementId = parameters["elementId"]?.Value<int>()
                ?? throw new ArgumentException("elementId requerido");

            var changes = parameters["changes"] as JArray
                ?? throw new ArgumentException("changes[] requerido");

            var element = document.GetElement(new ElementId(elementId));
            if (element == null)
                throw new InvalidOperationException($"Elemento {elementId} no encontrado");

            var results = new List<object>();

            using (var tx = new Transaction(document, "MCP: modify_element"))
            {
                tx.Start();
                foreach (JObject change in changes)
                {
                    var paramName = change["parameterName"]?.Value<string>()
                        ?? throw new ArgumentException("parameterName requerido en cada change");
                    var rawValue = change["value"]?.ToString()
                        ?? throw new ArgumentException("value requerido en cada change");

                    var param = element.LookupParameter(paramName);
                    if (param == null)
                    {
                        results.Add(new { parameterName = paramName, status = "error", message = "Parámetro no encontrado" });
                        continue;
                    }
                    if (param.IsReadOnly)
                    {
                        results.Add(new { parameterName = paramName, status = "error", message = "Parámetro es de solo lectura" });
                        continue;
                    }

                    try
                    {
                        switch (param.StorageType)
                        {
                            case StorageType.String:
                                param.Set(rawValue);
                                break;
                            case StorageType.Integer:
                                param.Set(int.Parse(rawValue));
                                break;
                            case StorageType.Double:
                                param.Set(double.Parse(rawValue,
                                    System.Globalization.CultureInfo.InvariantCulture));
                                break;
                            case StorageType.ElementId:
                                param.Set(new ElementId(int.Parse(rawValue)));
                                break;
                            default:
                                results.Add(new { parameterName = paramName, status = "error",
                                    message = $"StorageType {param.StorageType} no soportado" });
                                continue;
                        }
                        results.Add(new { parameterName = paramName, status = "ok",
                            storageType = param.StorageType.ToString() });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new { parameterName = paramName, status = "error",
                            message = ex.Message });
                    }
                }
                tx.Commit();
            }

            return new { success = true, elementId, changes = results };
        }
    }
}
```

- [ ] **Step 3: Registrar el comando en commandRegistry.json**

Agregar la entrada al final del array `Commands` en:
`C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Commands\commandRegistry.json`

```json
{
  "commandName": "modify_element",
  "assemblyPath": "RevitMCPCommandSet\\{VERSION}\\RevitMCPCommandSet.dll",
  "enabled": true,
  "supportedRevitVersions": ["2025"],
  "developer": {
    "name": "DPB",
    "email": "",
    "website": "",
    "organization": "Desarrollos Palo Blanco"
  },
  "description": "Modify parameters of an existing Revit element"
}
```

- [ ] **Step 4: Compilar el C#**

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP"
# Ajustar ruta al .csproj según la estructura real del repo
& $msb "revit-plugin\RevitMCPCommandSet\RevitMCPCommandSet.csproj" /p:Configuration=Release /t:Build
```

Esperado: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Desplegar el DLL compilado**

```powershell
$src = "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\revit-plugin\RevitMCPCommandSet\bin\Release\RevitMCPCommandSet.dll"
$dst = "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Commands\RevitMCPCommandSet\2025\"
Copy-Item $src $dst -Force
Write-Host "Deploy OK: $((Get-Item "$dst\RevitMCPCommandSet.dll").LastWriteTime)"
```

- [ ] **Step 6: Reiniciar Revit y activar el switch MCP**

Cerrar Revit completamente. Abrir Revit 2025. Abrir un proyecto con elementos. Activar el switch del add-in `mcp-servers-for-revit` en su pestaña.

- [ ] **Step 7: Verificar que el comando cargó**

```powershell
$log = Get-ChildItem "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Logs\" |
       Sort-Object LastWriteTime | Select-Object -Last 1
Select-String "modify_element" $log.FullName
```

Esperado: sin línea de `Failed to create command instance [modify_element]`. Si aparece, el error es en el C# - leer el log completo para la excepción.

- [ ] **Step 8: Loop de verificación - probar desde Claude**

Pedir a Claude que:
1. Llame `get_selected_elements` para obtener un ID real
2. Llame `modify_element` con:
   ```json
   {"elementId": ID_REAL, "changes": [{"parameterName": "Comments", "value": "Test MCP modify"}]}
   ```
3. Verifique en Revit que el parámetro cambió

Si falla: leer el log (`mcp_YYYYMMDD.log`), corregir el C#, volver al Step 4. Repetir hasta que funcione.

- [ ] **Step 9: Commit del C#**

```bash
git add revit-plugin/RevitMCPCommandSet/Commands/ModifyElementCommand.cs
git commit -m "feat(revit): implement modify_element command with multi-type parameter support"
```

---

## Task 3: Implementar modify_element en el servidor TypeScript

**Files:**
- Modifica: `mcp-server/src/tools/modify_element.ts`
- Modifica: `mcp-server/src/tools/register.ts` (si el stub no está ya registrado)

**Interfaces:**
- Consumes: `withRevitConnection` de `../utils/ConnectionManager.js` (patrón idéntico a `operate_element.ts`)
- Produce: tool MCP `modify_element` registrado en el servidor

- [ ] **Step 1: Leer el stub actual**

```powershell
Get-Content "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\mcp-server\src\tools\modify_element.ts"
```

Si es `export {};`, reemplazarlo completamente.

- [ ] **Step 2: Implementar modify_element.ts**

```typescript
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerModifyElementTool(server: any) {
  server.tool(
    "modify_element",
    "Modify one or more parameters of an existing Revit element by its element ID. " +
    "Accepts a list of parameter changes, each with a parameter name and new value. " +
    "Values are always passed as strings; the add-in converts to the correct StorageType " +
    "(String, Integer, Double, ElementId). Read-only parameters are skipped with an error status.",
    {
      elementId: z
        .number()
        .int()
        .describe("Revit element ID of the element to modify"),
      changes: z
        .array(
          z.object({
            parameterName: z
              .string()
              .describe("Exact name of the parameter to modify"),
            value: z
              .union([z.string(), z.number()])
              .describe(
                "New value for the parameter. Pass numbers as numbers, text as strings. " +
                "For Double parameters Revit uses internal units (feet); pass values already converted."
              ),
          })
        )
        .min(1)
        .describe("List of parameter changes to apply to the element"),
    },
    async (args: { elementId: number; changes: { parameterName: string; value: string | number }[] }) => {
      const params = {
        elementId: args.elementId,
        changes: args.changes.map((c) => ({
          parameterName: c.parameterName,
          value: String(c.value),
        })),
      };

      try {
        const response = await withRevitConnection(async (revitClient: any) => {
          return await revitClient.sendCommand("modify_element", params);
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `modify_element failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
```

- [ ] **Step 3: Verificar que el tool está en register.ts**

```powershell
Select-String "modify_element" "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\mcp-server\src\tools\register.ts"
```

Si no aparece, agregar el import y la llamada:

```typescript
import { registerModifyElementTool } from "./modify_element.js";
// dentro de registerTools(server):
registerModifyElementTool(server);
```

- [ ] **Step 4: Compilar el servidor TypeScript**

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\mcp-server"
npm run build
```

Esperado: sin errores de TypeScript.

- [ ] **Step 5: Reiniciar Claude Code y verificar que la herramienta aparece**

Reiniciar Claude Code para que recargue el servidor MCP local. Confirmar que `modify_element` aparece en la lista de herramientas disponibles del servidor.

- [ ] **Step 6: Test de integración end-to-end**

Pedirle a Claude:
> "Selecciona un elemento en Revit, obtén su ID, y usa modify_element para cambiar su parámetro 'Comments' a 'Verificado DPB'."

Esperado: `{"success": true, "changes": [{"parameterName": "Comments", "status": "ok"}]}`. Verificar en Revit.

Si falla: revisar si el problema es en la capa TypeScript (error antes de llegar a Revit) o en la capa C# (error en el log del add-in). Corregir la capa afectada y repetir.

- [ ] **Step 7: Commit**

```bash
git add mcp-server/src/tools/modify_element.ts mcp-server/src/tools/register.ts
git commit -m "feat(mcp): implement modify_element tool with zod schema validation"
```

---

## Task 4: Implementar GetParameterCommand en C#

**Files:**
- Crea: `revit-plugin/RevitMCPCommandSet/Commands/GetParameterCommand.cs`

**Interfaces:**
- Consumes: firma de `IRevitCommand` (misma de Task 2)
- Produce: clase `GetParameterCommand` que devuelve `{name, value, storageType, isReadOnly, hasValue}`

- [ ] **Step 1: Crear GetParameterCommand.cs**

```csharp
using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Interfaces;  // ajustar namespace según repo

namespace RevitMCPCommandSet.Commands
{
    public class GetParameterCommand : IRevitCommand
    {
        public string CommandName => "get_parameter";

        public object Execute(JObject parameters, Document document)
        {
            var elementId = parameters["elementId"]?.Value<int>()
                ?? throw new ArgumentException("elementId requerido");

            var element = document.GetElement(new ElementId(elementId));
            if (element == null)
                throw new InvalidOperationException($"Elemento {elementId} no encontrado");

            // Si se pide un parámetro específico
            var paramName = parameters["parameterName"]?.Value<string>();
            if (paramName != null)
            {
                var param = element.LookupParameter(paramName);
                if (param == null)
                    throw new InvalidOperationException($"Parámetro '{paramName}' no encontrado en elemento {elementId}");
                return SerializeParameter(param);
            }

            // Si no se especifica nombre, devolver todos los parámetros
            var allParams = new List<object>();
            foreach (Parameter p in element.Parameters)
            {
                allParams.Add(SerializeParameter(p));
            }
            return new { elementId, parameters = allParams };
        }

        private static object SerializeParameter(Parameter param)
        {
            string value = null;
            try
            {
                switch (param.StorageType)
                {
                    case StorageType.String:
                        value = param.AsString();
                        break;
                    case StorageType.Integer:
                        value = param.AsInteger().ToString();
                        break;
                    case StorageType.Double:
                        value = param.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case StorageType.ElementId:
                        value = param.AsElementId()?.IntegerValue.ToString();
                        break;
                }
            }
            catch { /* parámetro sin valor legible */ }

            return new
            {
                name = param.Definition.Name,
                value,
                valueString = param.HasValue ? param.AsValueString() : null,
                storageType = param.StorageType.ToString(),
                isReadOnly = param.IsReadOnly,
                hasValue = param.HasValue,
            };
        }
    }
}
```

- [ ] **Step 2: Registrar en commandRegistry.json**

Agregar al array `Commands`:

```json
{
  "commandName": "get_parameter",
  "assemblyPath": "RevitMCPCommandSet\\{VERSION}\\RevitMCPCommandSet.dll",
  "enabled": true,
  "supportedRevitVersions": ["2025"],
  "developer": {
    "name": "DPB",
    "email": "",
    "website": "",
    "organization": "Desarrollos Palo Blanco"
  },
  "description": "Read parameters from a Revit element"
}
```

- [ ] **Step 3: Compilar y desplegar**

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msb "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\revit-plugin\RevitMCPCommandSet\RevitMCPCommandSet.csproj" /p:Configuration=Release /t:Build

$src = "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\revit-plugin\RevitMCPCommandSet\bin\Release\RevitMCPCommandSet.dll"
$dst = "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Commands\RevitMCPCommandSet\2025\"
Copy-Item $src $dst -Force
```

- [ ] **Step 4: Reiniciar Revit, verificar log**

Verificar que no aparece `Failed to create command instance [get_parameter]` en el log.

- [ ] **Step 5: Commit del C#**

```bash
git add revit-plugin/RevitMCPCommandSet/Commands/GetParameterCommand.cs
git commit -m "feat(revit): implement get_parameter command with full parameter serialization"
```

---

## Task 5: Implementar get_parameter en el servidor TypeScript

**Files:**
- Crea: `mcp-server/src/tools/get_parameter.ts`
- Modifica: `mcp-server/src/tools/register.ts`

**Interfaces:**
- Consumes: `withRevitConnection` de `../utils/ConnectionManager.js`
- Produce: tool MCP `get_parameter`

- [ ] **Step 1: Crear get_parameter.ts**

```typescript
import { z } from "zod";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetParameterTool(server: any) {
  server.tool(
    "get_parameter",
    "Read one or all parameters from a Revit element. " +
    "If parameterName is provided, returns that specific parameter with its value, storageType, and readOnly flag. " +
    "If parameterName is omitted, returns all parameters on the element. " +
    "Values are returned both as raw internal value (value) and formatted display string (valueString).",
    {
      elementId: z
        .number()
        .int()
        .describe("Revit element ID"),
      parameterName: z
        .string()
        .optional()
        .describe(
          "Name of a specific parameter to read. Omit to get all parameters on the element."
        ),
    },
    async (args: { elementId: number; parameterName?: string }) => {
      const params = {
        elementId: args.elementId,
        parameterName: args.parameterName,
      };

      try {
        const response = await withRevitConnection(async (revitClient: any) => {
          return await revitClient.sendCommand("get_parameter", params);
        });
        return {
          content: [{ type: "text", text: JSON.stringify(response, null, 2) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get_parameter failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
```

- [ ] **Step 2: Agregar a register.ts**

```typescript
import { registerGetParameterTool } from "./get_parameter.js";
// dentro de registerTools(server):
registerGetParameterTool(server);
```

- [ ] **Step 3: Compilar**

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\mcp-server"
npm run build
```

- [ ] **Step 4: Test end-to-end**

Pedirle a Claude:
> "Lee el parámetro 'Level' del elemento con ID X en Revit."

Esperado: JSON con `{name: "Level", value: "...", storageType: "ElementId", isReadOnly: true, hasValue: true}`.

Si falla: misma estrategia - identificar en qué capa falla (TypeScript vs C#) y corregir.

- [ ] **Step 5: Commit**

```bash
git add mcp-server/src/tools/get_parameter.ts mcp-server/src/tools/register.ts
git commit -m "feat(mcp): add get_parameter tool for reading element parameters"
```

---

## Task 6: Soporte Revit 2026

**Files:**
- Modifica: `revit-plugin/RevitMCPCommandSet/RevitMCPCommandSet.csproj` (agregar build target para 2026)
- Crea: `C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2026\revit_mcp_plugin\` (estructura completa)

**Interfaces:**
- Produce: add-in instalado y funcional en Revit 2026 con todas las herramientas

- [ ] **Step 1: Verificar diferencias entre APIs 2025 y 2026**

```powershell
# Comparar tamaños como indicador de cambios
"RevitAPI 2025: $((Get-Item 'C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll').Length) bytes"
"RevitAPI 2026: $((Get-Item 'C:\Program Files\Autodesk\Revit 2026\RevitAPI.dll').Length) bytes"
```

- [ ] **Step 2: Crear una copia del proyecto apuntando a 2026**

Duplicar el `.csproj` como `RevitMCPCommandSet.2026.csproj` y cambiar las referencias:

```xml
<!-- Cambiar -->
<HintPath>C:\Program Files\Autodesk\Revit 2025\RevitAPI.dll</HintPath>
<HintPath>C:\Program Files\Autodesk\Revit 2025\RevitAPIUI.dll</HintPath>
<!-- Por -->
<HintPath>C:\Program Files\Autodesk\Revit 2026\RevitAPI.dll</HintPath>
<HintPath>C:\Program Files\Autodesk\Revit 2026\RevitAPIUI.dll</HintPath>
```

- [ ] **Step 3: Compilar para 2026**

```powershell
$msb = "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
& $msb "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\revit-plugin\RevitMCPCommandSet\RevitMCPCommandSet.2026.csproj" /p:Configuration=Release /t:Build /p:OutputPath="bin\Release2026\"
```

Si hay errores de compilación por cambios de API 2026: leer los errores, ajustar el código C# para compatibilidad, repetir.

- [ ] **Step 4: Crear la estructura de carpetas para 2026**

```powershell
$base = "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2026\revit_mcp_plugin"
New-Item -ItemType Directory -Force "$base\Commands\RevitMCPCommandSet\2026"
New-Item -ItemType Directory -Force "$base\Logs"

# Copiar DLLs del núcleo (desde la instalación 2025)
$src25 = "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin"
Copy-Item "$src25\RevitMCPPlugin.dll" $base
Copy-Item "$src25\RevitMCPSDK.dll" $base
Copy-Item "$src25\Newtonsoft.Json.dll" $base
Copy-Item "$src25\Microsoft.Windows.SDK.NET.dll" $base
Copy-Item "$src25\WinRT.Runtime.dll" $base
Copy-Item "$src25\Commands\commandRegistry.json" "$base\Commands\"
Copy-Item "$src25\Commands\RevitMCPCommandSet\command.json" "$base\Commands\RevitMCPCommandSet\"

# Copiar DLLs del CommandSet compilado para 2026
$cmdSrc = "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Commands\RevitMCPCommandSet\2025"
Copy-Item "$cmdSrc\*" "$base\Commands\RevitMCPCommandSet\2026\" -Recurse

# Sobreescribir con el DLL compilado para 2026
Copy-Item "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\revit-plugin\RevitMCPCommandSet\bin\Release2026\RevitMCPCommandSet.dll" "$base\Commands\RevitMCPCommandSet\2026\"
```

- [ ] **Step 5: Crear el manifiesto del add-in para 2026**

Crear `C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2026\mcp-servers-for-revit.addin`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>mcp-servers-for-revit</Name>
    <Assembly>revit_mcp_plugin/RevitMCPPlugin.dll</Assembly>
    <AddInId>090a4c8c-61dc-426d-87df-e4bae0f80ec1</AddInId>
    <FullClassName>revit_mcp_plugin.Core.Application</FullClassName>
    <SuppressedWarning>GUIDConflict</SuppressedWarning>
    <VendorId>mcp-servers-for-revit</VendorId>
    <VendorDescription>https://github.com/mcp-servers-for-revit/mcp-servers-for-revit</VendorDescription>
  </AddIn>
</RevitAddIns>
```

**Nota:** El GUID es el mismo que en 2025. Si Revit rechaza el GUID duplicado al tener ambas versiones abiertas simultáneamente, cambiar el último octeto del GUID (ej: `...0ec2`).

- [ ] **Step 6: Actualizar commandRegistry.json de 2026**

En `...\Addins\2026\revit_mcp_plugin\Commands\commandRegistry.json`, cambiar todos los `"supportedRevitVersions"` de `["2025"]` a `["2026"]`.

- [ ] **Step 7: Verificar en Revit 2026**

Abrir Revit 2026. Habilitar el switch del add-in. Verificar en el log (`Logs\mcp_YYYYMMDD.log`) que dice `Current Revit version: 2026` y que carga los comandos sin errores.

Llamar `say_hello` desde Claude para confirmar la conexión.

- [ ] **Step 8: Loop si hay errores**

Si `revit_mcp_plugin.Core.Application` falla al cargar en 2026 por cambios internos de Revit API: el `RevitMCPPlugin.dll` principal (que inicializa el WebSocket) quizás también necesita recompilación para 2026. En ese caso, compilar también el proyecto `RevitMCPPlugin` con referencias a Revit 2026 API.

- [ ] **Step 9: Commit**

```bash
git add revit-plugin/RevitMCPCommandSet/RevitMCPCommandSet.2026.csproj
git commit -m "feat(revit): add Revit 2026 build target and deployment structure"
```

---

## Task 7: Verificación final y loop

**Propósito:** Confirmar que las 3 herramientas nuevas funcionan end-to-end en Revit 2025. Si algo falla, este task describe cómo diagnosticar y volver al task correspondiente.

**Checklist de verificación:**

- [ ] **1. modify_element: cambio de parámetro texto**
  - Seleccionar un muro en Revit
  - Llamar: `modify_element({elementId: ID, changes: [{parameterName: "Comments", value: "OK-modify_element"}]})`
  - Verificar en propiedades de Revit: Comments = "OK-modify_element"

- [ ] **2. modify_element: cambio de parámetro numérico**
  - Identificar un parámetro de tipo Double en el muro (ej: "Unconnected Height")
  - Llamar: `modify_element({elementId: ID, changes: [{parameterName: "Unconnected Height", value: "9.84252"}]})`
  - 9.84252 pies = 3000 mm. Verificar en Revit.

- [ ] **3. modify_element: manejo de error en parámetro inexistente**
  - Llamar: `modify_element({elementId: ID, changes: [{parameterName: "NoExiste", value: "x"}]})`
  - Esperado: `{"success": true, "changes": [{"parameterName": "NoExiste", "status": "error", "message": "Parámetro no encontrado"}]}`

- [ ] **4. get_parameter: lectura de parámetro específico**
  - Llamar: `get_parameter({elementId: ID, parameterName: "Comments"})`
  - Esperado: `{name: "Comments", value: "OK-modify_element", storageType: "String", isReadOnly: false}`

- [ ] **5. get_parameter: listado completo de parámetros**
  - Llamar: `get_parameter({elementId: ID})`
  - Esperado: array de todos los parámetros del elemento

- [ ] **6. Combinación: leer → modificar → verificar**
  - `get_parameter` → identifica un parámetro editable → `modify_element` lo cambia → `get_parameter` confirma el nuevo valor

**Diagnóstico si un tool falla:**

```powershell
# Leer el log del add-in para el error exacto
$log = Get-ChildItem "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Logs\" |
       Sort-Object LastWriteTime | Select-Object -Last 1
Get-Content $log.FullName | Select-String "Error|Exception|Failed|modify_element|get_parameter"
```

Si el error está en C#: volver al Task 2 Step 2 (modify) o Task 4 Step 1 (get_parameter), corregir, Task 2 Steps 4-5 (build + deploy), reiniciar Revit.

Si el error está en TypeScript (no llega a Revit): revisar el `build/tools/modify_element.js` compilado y el error en la consola del MCP.

---

## Orden de ejecución recomendado

```
Task 0 → Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 7 → Task 6 → Task 7 (2026)
         (prototipo)  (C# modify) (TS modify) (C# get_param) (TS get_param) (verificar) (Revit 2026)
```

Task 6 (Revit 2026) puede ejecutarse de forma independiente después de que Task 3 esté completo.
