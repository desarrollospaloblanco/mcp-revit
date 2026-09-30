---
name: instalar-mcp-revit
description: Compilar, instalar o actualizar el add-in de Revit de este fork y conectar el servidor MCP en una máquina, incluida una instalación desde cero tras clonar el repo. Usar cuando el usuario pide instalar, desplegar, actualizar o "hacer que aparezcan" los comandos del MCP en Revit o en Claude.
---

# Instalar y desplegar mcp-revit

## Requisitos de la máquina

- Revit 2023–2026, .NET SDK (`dotnet`), Node.js 20+ y Git.
- Para el extractor de PDF: Python 3 con `pip install -r tools/pdf-to-revit/requirements.txt`.
- Compilar con `dotnet build`, **no con msbuild**: el MSBuild de Build Tools puede no traer el SDK
  de .NET y falla con MSB4236 en los destinos net48 (Revit 2023/2024).

## Instalación o actualización (Revit cerrado)

```powershell
git clone https://github.com/desarrollospaloblanco/mcp-revit.git
cd mcp-revit
.\scripts\install-addin.ps1 -RevitVersion 2025 -Build
```

El script compila el add-in y el servidor, respalda lo instalado en
`%USERPROFILE%\mcp-revit-backups\`, copia el add-in a `%APPDATA%\Autodesk\Revit\Addins\<año>\` y
**registra todos los comandos en `commandRegistry.json`**. El plugin solo carga lo que está en ese
registro; con `command.json` no basta, y una instalación nueva empieza con el registro vacío.
Con `-DryRun` muestra lo que haría sin tocar nada.

Revit bloquea las DLL que cargó: el script se niega a correr con Revit abierto. Para cerrarlo sin
perder trabajo, guardar primero los documentos (sin guardar, Revit pregunta al cerrar).

## Conectar el cliente MCP

Apuntar al servidor compilado localmente, no a `npx mcp-server-for-revit`: el paquete publicado
es el del upstream y no trae los comandos de este fork.

```powershell
claude mcp add mcp-server-for-revit -s user -- node "<ruta>\mcp-revit\server\build\index.js"
```

En Claude Desktop, lo mismo en `claude_desktop_config.json`. Después, reiniciar el cliente para que
lea el esquema nuevo de las herramientas.

## Encender el servicio en Revit

Abrir Revit y pulsar **"Revit MCP Switch"** en el panel "Revit MCP Plugin" (pestaña
Complementos). El servicio escucha en `localhost:8080`. Esto lo hace el usuario: no automatizarlo
con un arranque automático sin que él lo decida.

Comprobar la conexión:

```powershell
node scripts/revit-call.mjs get_current_view_info "{}" 30
```

## Después de cambiar el código

1. `dotnet build mcp-servers-for-revit.sln -c "Release R25"` (y los demás años que se usen).
2. Cerrar Revit, correr `.\scripts\install-addin.ps1 -RevitVersion 2025`, abrir Revit y pulsar el
   switch.
3. Si cambió el esquema de una herramienta: `npm run build` en `server/` y reiniciar el cliente
   MCP. Mientras tanto, `scripts/revit-call.mjs` habla directo con el socket y no depende del
   esquema.

## Diagnóstico

- El registro del plugin (`revit_mcp_plugin\Logs\mcp_*.log`) dice "Failed to create command
  instance" justo **después** de registrar un comando con éxito: es un error de etiquetado
  heredado, no una falla.
- Un cliente de Node que "tarda" exactamente su timeout aunque Revit respondió: un temporizador sin
  cancelar retiene el proceso (corregido en `server/src/utils/SocketClient.ts`). Mirar el diario de
  Revit antes de culpar a Revit.
- Una carpeta suelta `Addins\<año>\RevitMCPCommandSet\` la escribe el SDK al compilar en Release;
  Revit no la carga porque no tiene `.addin`. No confundirla con la instalación real.
