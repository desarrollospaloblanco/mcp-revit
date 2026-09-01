# Revit MCP - Features Faltantes

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:subagent-driven-development` (recommended) or `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extender el fork local de `mcp-server-for-revit` para implementar `modify_element`, `get_parameter`, `set_parameter` y soporte para Revit 2026, logrando un toolset completo y funcional sin depender de `send_code_to_revit` como workaround para operaciones de parámetros.

**Architecture:** El repositorio tiene tres componentes acoplados: un servidor MCP en TypeScript/Node.js (`server/`), el host del add-in en C# (`plugin/`, que expone el socket y la UI) y el set de comandos en C# (`commandset/`, que compila a `RevitMCPCommandSet.dll`). Cada nueva herramienta requiere implementación en dos lados: un par **Command + EventHandler** en el commandset, y un archivo `.ts` que registra el tool en el servidor MCP. La comunicación es un **socket TCP plano en `localhost:8080` hablando JSON-RPC 2.0** (un objeto JSON por mensaje), no WebSocket.

El ciclo de desarrollo real es: prototipo con `send_code_to_revit` → implementar C# → **cerrar Revit** → compilar (el build despliega solo) → abrir Revit → verificar → iterar. Cerrar Revit es obligatorio, no opcional: mientras corre mantiene bloqueado el DLL y el paso de copia del build falla.

**Tech Stack:** TypeScript + Node.js (MCP server), C# / .NET 8 (Revit add-in para 2025 y 2026), .NET SDK, Revit API 2025 + 2026 vía NuGet, zod (validación de esquemas), Newtonsoft.Json (serialización C#).

**Spec:** `docs/superpowers/plans/2026-08-31-revit-mcp-features.md` (este archivo)

> **Corregido tras inspeccionar el repo (2026-09-01).** El plan original se escribió sobre supuestos que no coinciden con el repositorio real. Las constantes, rutas y comandos de abajo ya están corregidos. Ver la sección [Desviaciones respecto al plan original](#desviaciones-respecto-al-plan-original) al final.

## Global Constraints

- C# target framework: **`net8.0-windows10.0.19041.0`** para Revit 2025 y 2026 (`net48` solo aplica a R20-R24)
- Build con **`dotnet build`**, no con MSBuild 2019: los proyectos son SDK-style y apuntan a .NET 8
- Configuraciones de build: `Debug R25`, `Release R25`, `Debug R26`, `Release R26` (ya existen en ambos `.csproj`)
- Las referencias a la API de Revit vienen por **NuGet** (`Nice3point.Revit.Api.RevitAPI`, `Nice3point.Revit.Api.RevitAPIUI`, `RevitMCPSDK`), no por `HintPath` a `C:\Program Files\Autodesk\`
- Las configuraciones `Debug *` **auto-despliegan** a `%AppData%\Autodesk\Revit\Addins\<version>\`; no hay que copiar DLLs a mano
- **Revit debe estar cerrado para compilar en `Debug`**: el add-in mantiene bloqueado `RevitMCPCommandSet.dll` y el copy step falla con `MSB3027`
- Logs del add-in: `%AppData%\Autodesk\Revit\Addins\<version>\revit_mcp_plugin\Logs\mcp_YYYYMMDD.log`
- Registro de comandos: `command.json` (raíz del repo, fuente de verdad) y `commandRegistry.json` (por versión, en `Addins\<version>\revit_mcp_plugin\Commands\`)
- Repositorio upstream: `https://github.com/mcp-servers-for-revit/mcp-servers-for-revit`
- Directorio de trabajo: `C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\`
- No mezclar cambios no relacionados en un mismo commit
- Verificar funcionamiento en Revit antes de marcar un task como completo

---

## Mapa de archivos

Estructura real verificada en el Task 0:

```
Revit-MCP/                              (directorio de trabajo)
├── command.json                        MODIFICAR: registrar los 3 comandos nuevos
├── mcp-servers-for-revit.sln
├── docs/superpowers/plans/             (este archivo)
├── server/                             (servidor MCP en TypeScript)
│   ├── src/
│   │   ├── tools/
│   │   │   ├── register.ts             SIN CAMBIOS: auto-descubre los archivos de tools/
│   │   │   ├── modify_element.ts       MODIFICAR: implementar (archivo vacío, 0 bytes)
│   │   │   ├── get_parameter.ts        CREAR
│   │   │   └── set_parameter.ts        CREAR
│   │   └── utils/ConnectionManager.ts  (withRevitConnection -> TCP localhost:8080)
│   ├── package.json
│   └── tsconfig.json
├── commandset/                         (add-in C#: comandos)
│   ├── RevitMCPCommandSet.csproj       SIN CAMBIOS: ya soporta R20-R26
│   ├── Models/Common/
│   │   └── ParameterInfo.cs            CREAR: modelos de parámetros
│   ├── Services/
│   │   ├── ParameterUtils.cs           CREAR: lógica compartida de lectura/escritura
│   │   ├── ModifyElementEventHandler.cs CREAR
│   │   ├── GetParameterEventHandler.cs  CREAR
│   │   └── SetParameterEventHandler.cs  CREAR
│   └── Commands/Parameters/            CREAR: carpeta nueva
│       ├── ParameterCommandParsing.cs  CREAR: parseo compartido de argumentos
│       ├── ModifyElementCommand.cs     CREAR (no existía ningún stub)
│       ├── GetParameterCommand.cs      CREAR
│       └── SetParameterCommand.cs      CREAR
└── plugin/                             (add-in C#: host, WebSocket, UI)
    ├── RevitMCPPlugin.csproj           SIN CAMBIOS: ya soporta R20-R26
    └── mcp-servers-for-revit.addin     SIN CAMBIOS: se despliega solo
```

**Patrón obligatorio de cada comando.** No existe `IRevitCommand.Execute(JObject, Document)`. Cada comando son **dos clases**:

1. `XCommand : ExternalEventCommandBase` - parsea el `JObject`, llena el handler, llama `RaiseAndWaitForCompletion(ms)` y devuelve el resultado.
2. `XEventHandler : IExternalEventHandler, IWaitableExternalEventHandler` - corre en el hilo de Revit, abre la `Transaction` y publica el resultado.

Referencia viva: `commandset/Commands/Delete/DeleteElementCommand.cs` + `commandset/Services/DeleteElementEventHandler.cs`.

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
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\server"  # O la ruta real
npm install
```

- [ ] **Step 5: Verificar build inicial del servidor TypeScript**

```powershell
npm run build
```

Esperado: BUILD SUCCESSFUL sin errores. Si falla, leer error y corregir dependencias.

- [ ] **Step 6: Verificar build inicial del add-in C#**

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP"
dotnet build commandset/RevitMCPCommandSet.csproj -c "Debug R25"
```

Esperado: `Build succeeded. 0 Error(s)`. Las referencias a la API de Revit se restauran por NuGet, no hay HintPath que revisar.

Si Revit 2025 está abierto, la compilación **sí funciona** pero el paso de copia falla con `MSB3027 ... The file is locked by: "Autodesk Revit"`. Eso no es un error de código: cerrar Revit y repetir.

- [ ] **Step 7: Configurar Claude Code para usar el servidor local en lugar del npm**

Editar `~/.claude.json` - cambiar la entrada `mcp-server-for-revit`:

```json
"mcp-server-for-revit": {
  "type": "stdio",
  "command": "node",
  "args": ["C:/Users/jborrayo.DPB/BIM Tools/Revit-MCP/server/build/index.js"],
  "env": {}
}
```

El path apunta al build local; hay que rehacerlo si el repositorio se mueve de carpeta.

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
- Modifica: `commandset/Commands/Parameters/ModifyElementCommand.cs` (o la ruta exacta del repo)

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

**Cerrar Revit primero.** Con Revit abierto el DLL está bloqueado y el despliegue falla.

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP"
dotnet build commandset/RevitMCPCommandSet.csproj -c "Debug R25"
```

Esperado: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Desplegar el DLL compilado**

No hay paso manual: el target `DeployCommandSet` del `.csproj` ya copió el DLL en el Step 4 porque la configuración es `Debug`. Confirmar la marca de tiempo:

```powershell
$dst = "$env:AppData\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Commands\RevitMCPCommandSet\2025\RevitMCPCommandSet.dll"
Write-Host "Deploy: $((Get-Item $dst).LastWriteTime)"
```

- [ ] **Step 6: Reiniciar Revit y activar el switch MCP**

Cerrar Revit completamente. Abrir Revit 2025. Abrir un proyecto con elementos. Activar el switch del add-in `mcp-servers-for-revit` en su pestaña.

- [ ] **Step 7: Verificar que el comando cargó**

```powershell
$log = Get-ChildItem "C:\Users\jborrayo.DPB\AppData\Roaming\Autodesk\Revit\Addins\2025\revit_mcp_plugin\Logs\" |
       Sort-Object LastWriteTime | Select-Object -Last 1
Select-String "modify_element" $log.FullName
```

**OJO - el log miente.** `CommandManager.LoadCommandFromAssembly` tiene un bug upstream ([plugin/Core/CommandManager.cs:166](../../../plugin/Core/CommandManager.cs)): usa `_logger.Info` con el texto `Failed to create command instance [...]` **en la rama de éxito**. Ver esa línea NO significa que falló.

Verificación real: la ausencia de una línea `_logger.Error` con el nombre completo del tipo (`RevitMCPCommandSet.Commands.Parameters.ModifyElementCommand`), y sobre todo que el comando responda por el socket. La verificación confiable es funcional, no por log.

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
git add commandset/Commands/Parameters/ModifyElementCommand.cs
git commit -m "feat(revit): implement modify_element command with multi-type parameter support"
```

---

## Task 3: Implementar modify_element en el servidor TypeScript

**Files:**
- Modifica: `server/src/tools/modify_element.ts`
- Modifica: `server/src/tools/register.ts` (si el stub no está ya registrado)

**Interfaces:**
- Consumes: `withRevitConnection` de `../utils/ConnectionManager.js` (patrón idéntico a `operate_element.ts`)
- Produce: tool MCP `modify_element` registrado en el servidor

- [ ] **Step 1: Leer el stub actual**

```powershell
Get-Content "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\server\src\tools\modify_element.ts"
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
Select-String "modify_element" "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\server\src\tools\register.ts"
```

Si no aparece, agregar el import y la llamada:

```typescript
import { registerModifyElementTool } from "./modify_element.js";
// dentro de registerTools(server):
registerModifyElementTool(server);
```

- [ ] **Step 4: Compilar el servidor TypeScript**

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\server"
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
git add server/src/tools/modify_element.ts
git commit -m "feat(mcp): implement modify_element tool with zod schema validation"
```

---

## Task 4: Implementar GetParameterCommand en C#

**Files:**
- Crea: `commandset/Commands/Parameters/GetParameterCommand.cs`

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

Con Revit cerrado, un solo comando compila y despliega:

```powershell
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP"
dotnet build commandset/RevitMCPCommandSet.csproj -c "Debug R25"
```

- [ ] **Step 4: Reiniciar Revit, verificar log**

Verificar que no aparece `Failed to create command instance [get_parameter]` en el log.

- [ ] **Step 5: Commit del C#**

```bash
git add commandset/Commands/Parameters/GetParameterCommand.cs
git commit -m "feat(revit): implement get_parameter command with full parameter serialization"
```

---

## Task 5: Implementar get_parameter en el servidor TypeScript

**Files:**
- Crea: `server/src/tools/get_parameter.ts`
- Modifica: `server/src/tools/register.ts`

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
cd "C:\Users\jborrayo.DPB\BIM Tools\Revit-MCP\server"
npm run build
```

- [ ] **Step 4: Test end-to-end**

Pedirle a Claude:
> "Lee el parámetro 'Level' del elemento con ID X en Revit."

Esperado: JSON con `{name: "Level", value: "...", storageType: "ElementId", isReadOnly: true, hasValue: true}`.

Si falla: misma estrategia - identificar en qué capa falla (TypeScript vs C#) y corregir.

- [ ] **Step 5: Commit**

```bash
git add server/src/tools/get_parameter.ts
git commit -m "feat(mcp): add get_parameter tool for reading element parameters"
```

---

## Task 6: Soporte Revit 2026

> **Reescrito el 2026-09-01.** El plan original pedía duplicar el `.csproj` con `HintPath` a la API de Revit 2026 y armar a mano la carpeta de Addins. Nada de eso hace falta: ambos `.csproj` ya traen configuraciones `Debug R26` / `Release R26` que resuelven la API por NuGet y despliegan solas.

**Files:**
- Ninguno. No se modifica ni se crea ningún archivo del repo.

**Interfaces:**
- Produce: add-in instalado y funcional en Revit 2026 con las 26 herramientas

- [x] **Step 1: Compilar el plugin host para 2026**

```powershell
dotnet build plugin/RevitMCPPlugin.csproj -c "Debug R26"
```

Esto compila `RevitMCPPlugin.dll` contra la API de Revit 2026 y copia el manifiesto `mcp-servers-for-revit.addin` más las DLLs del núcleo a `%AppData%\Autodesk\Revit\Addins\2026\`.

- [x] **Step 2: Compilar el commandset para 2026**

```powershell
dotnet build commandset/RevitMCPCommandSet.csproj -c "Debug R26"
```

Esto copia las DLLs de comandos a `...\Addins\2026\revit_mcp_plugin\Commands\RevitMCPCommandSet\2026\` y el `command.json` a la carpeta padre.

**Resultado:** ambos proyectos compilan para Revit 2026 con **0 errores**. No hubo ningún cambio de API entre 2025 y 2026 que afectara este código, así que el Step 8 del plan original (recompilar por incompatibilidades) resultó innecesario.

- [x] **Step 3: Generar el commandRegistry.json de 2026**

Es el único archivo que ningún build despliega: `PathManager.GetCommandRegistryFilePath` lo crea vacío si no existe, y `CommandManager.LoadCommands` solo carga lo que esté listado ahí. Hay que generarlo desde `command.json` con `supportedRevitVersions: ["2026"]` en cada entrada, y con `assemblyPath` igual a `RevitMCPCommandSet\\{VERSION}\\RevitMCPCommandSet.dll`.

Destino: `%AppData%\Autodesk\Revit\Addins\2026\revit_mcp_plugin\Commands\commandRegistry.json`

- [ ] **Step 4: Verificar en Revit 2026**

Abrir Revit 2026, abrir un proyecto, activar el switch del add-in. En `Logs\mcp_YYYYMMDD.log` debe decir `Current Revit version: 2026` y cargar los 26 comandos.

**Nota sobre el GUID:** el manifiesto usa el mismo `ClientId` (`090A4C8C-...`) en 2025 y 2026. No es un conflicto: Revit resuelve los add-ins por versión y cada una lee su propia carpeta de Addins. Solo habría que cambiarlo si Revit rechazara la carga por GUID duplicado.

- [ ] **Step 5: Sin commit**

Task 6 no toca archivos del repo, así que no genera commit.

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

---

## Desviaciones respecto al plan original

Registro de lo que el plan original daba por hecho y lo que el repositorio realmente exige. Verificado el 2026-09-01 contra el commit `f7fdd74`.

| # | El plan original decía | La realidad | Impacto |
|---|---|---|---|
| 1 | Target `net48` para todo | `net8.0-windows10.0.19041.0` en R25/R26; `net48` solo en R20-R24 | Alto |
| 2 | Compilar con MSBuild 2019 BuildTools | Es SDK-style y apunta a .NET 8: MSBuild 2019 no puede. Se usa `dotnet build` | Alto |
| 3 | Referenciar `RevitAPI.dll` por `HintPath` a `C:\Program Files\Autodesk\` | Todo viene por NuGet (`Nice3point.Revit.Api.*`, `RevitMCPSDK`) | Alto |
| 4 | Crear `RevitMCPCommandSet.2026.csproj` para Revit 2026 | Ambos `.csproj` ya traen `Debug R26` / `Release R26`. Duplicar habría sido un error | Alto |
| 5 | Implementar `IRevitCommand.Execute(JObject, Document)` | El patrón es `ExternalEventCommandBase` + `IExternalEventHandler` (dos clases por comando) | Alto |
| 6 | Editar `register.ts` para registrar cada tool | `register.ts` auto-descubre los archivos de `tools/`. No se toca | Medio |
| 7 | Copiar el DLL a mano a la carpeta de Addins | Las configuraciones `Debug *` auto-despliegan vía el target `DeployCommandSet` / `CopyFiles` | Medio |
| 8 | Armar a mano la carpeta `Addins\2026\` y el `.addin` | El build `Debug R26` del plugin lo hace solo | Medio |
| 9 | Verificar la carga leyendo `Failed to create command instance` en el log | Bug upstream: ese texto se loguea también en el caso de éxito. La verificación por log es inservible | Medio |
| 10 | Estructura `mcp-server/` y `revit-plugin/RevitMCPCommandSet/` | Es `server/`, `plugin/` y `commandset/` | Bajo |
| 11 | En `send_code_to_revit` el documento es `doc` | Es `document`, y en modo `auto` ya hay una `Transaction` abierta | Bajo |
| 12 | `ModifyElementCommand.cs` es un stub a completar | No existía ningún archivo. `modify_element.ts` sí existía, vacío (0 bytes) | Bajo |
| 13 | La comunicación es WebSocket | Es un socket TCP plano con JSON-RPC 2.0 | Bajo |

### Decisiones tomadas durante la ejecución

- **`set_parameter` sí se implementa.** El plan lo nombraba en el objetivo y en el mapa de archivos pero no tenía ningún task que lo construyera. Se implementó completo (C# + TS) con un alcance distinto al de `modify_element` para que no sea redundante: `set_parameter` escribe **un parámetro en muchos elementos**, `modify_element` escribe **muchos parámetros en un elemento**.
- **`useDisplayUnits`.** El Task 1 Step 4 del plan pedía documentar el problema de unidades. La solución quedó en el código: por defecto los `Double` se escriben en unidades internas de Revit (pies), y con `useDisplayUnits: true` el valor se interpreta en las unidades del proyecto vía `SetValueString`. Sin esto, escribir "3000" pensando en milímetros produce un valor 3000 veces mayor al esperado.
- **Rollback en fallo total.** Si ningún cambio de una llamada tuvo éxito, la transacción hace `RollBack()` en vez de `Commit()`, para no dejar pasos de deshacer vacíos en el historial de Revit.
- **Errores por cambio, no por llamada.** Un parámetro inexistente o de solo lectura devuelve `status: "error"` en su propia entrada y no aborta el resto del lote.
- **Convención de `ElementId`.** Se sigue el patrón del repo con `#if REVIT2024_OR_GREATER` para usar la API basada en `Int64` en 2024+ y la de `int` en versiones anteriores.

### Hallazgos del prototipo (Task 1)

Medidos sobre el muro `181178` de un proyecto real:

- Las unidades internas de Revit son **pies**; las de display del proyecto eran **metros** (`AsDouble() = -0.0656` para un `AsValueString() = "-0.02"`).
- `SetValueString("100")` interpreta unidades de proyecto y devuelve `bool` de éxito.
- Los 4 `StorageType` aparecen en un solo muro: `String` (Comments, Mark), `Integer` (Structural, Cross-Section), `Double` (Top Offset, Base Offset), `ElementId` (Phase Created, Family and Type).
- `LookupParameter` devuelve `null` si el parámetro no existe, y `IsReadOnly` detecta correctamente los calculados (`Volume`).
- `get_Parameter(BuiltInParameter)` sirve de respaldo cuando el nombre viene localizado.
