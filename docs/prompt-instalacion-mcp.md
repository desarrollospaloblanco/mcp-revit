# Instalar el MCP de Revit en otra máquina

Prompt para que cualquier persona de DPB instale el MCP de Revit en su propia máquina y su propia cuenta de Claude. Todo se descarga del release más reciente del repo público [`desarrollospaloblanco/mcp-revit`](https://github.com/desarrollospaloblanco/mcp-revit/releases): add-in ya compilado para Revit 2023 a 2026, servidor MCP con sus dependencias, scripts, extractor de PDF y skills de Claude. No hace falta clonar, compilar ni tener cuenta de GitHub.

## Antes de empezar (lo hace la persona, no Claude)

| Requisito | Detalle |
|---|---|
| Claude Code | Instalado (app de escritorio, pestaña Code, o CLI) y con sesión iniciada en su cuenta. |
| Revit | 2023, 2024, 2025 o 2026 instalado. **Cerrado** durante la instalación. |
| Permisos | Poder instalar programas con winget si le falta Node.js. |

## Cómo usarlo

1. Abrir Claude Code en cualquier carpeta (por ejemplo `C:\Users\<usuario>`).
2. Copiar el bloque de abajo completo y pegarlo como primer mensaje.
3. Atender lo que Claude pida: cerrar Revit, elegir versiones.
4. Al final: abrir Revit, pulsar **Revit MCP Switch** y reiniciar Claude.

---

## Prompt de instalación

```text
Necesito que instales en esta máquina el MCP de Revit de Desarrollos Palo Blanco desde el último release del repo público https://github.com/desarrollospaloblanco/mcp-revit. Todo se baja de ese release: no clones el repo ni compiles nada. Háblame en español. Sigue estos pasos en orden, explícame en una línea qué haces en cada uno y detente a preguntarme cuando un paso lo indique.

REGLAS
- No escribas contraseñas, tokens ni credenciales por mí. El repo es público: no hace falta GitHub CLI ni iniciar sesión.
- No cierres Revit tú. Si está abierto, pídeme que guarde y lo cierre, y espera.
- No actives el servicio de Revit automáticamente ni configures arranques automáticos; el botón "Revit MCP Switch" lo pulso yo.
- No uses el paquete npm publicado (npx mcp-server-for-revit): es el del proyecto original y no trae los comandos de DPB. Siempre el servidor que viene en el release.
- Antes de instalar cualquier programa con winget, dime cuál y espera mi visto bueno.
- No borres carpetas ni archivos existentes sin preguntarme.
- Si un paso falla, muéstrame el error real y propón la solución; no sigas como si hubiera funcionado.

PASO 1 - Requisitos
Comprueba la versión de node (>= 20) y si hay python (solo para el extractor de PDF). Muéstrame una tabla con lo que hay y lo que falta. Para lo que falte propón el comando winget (OpenJS.NodeJS.LTS, Python.Python.3.12) y espera mi confirmación. Después de instalar puede hacer falta abrir una terminal nueva para que se actualice el PATH.

PASO 2 - Última versión
Obtén la versión más reciente desde la API pública de GitHub (sin iniciar sesión):
  (Invoke-RestMethod https://api.github.com/repos/desarrollospaloblanco/mcp-revit/releases/latest).tag_name
Si falla por red o proxy, muéstrame el error y detente. Si no hay ningún release publicado, dímelo y detente.

PASO 3 - Descargar y extraer
La carpeta de instalación es %LOCALAPPDATA%\mcp-revit\<tag> (por ejemplo %LOCALAPPDATA%\mcp-revit\v1.2.0). Si ya existe, dime que esa versión ya está descargada y pregúntame si la reutilizo. Si no existe:
  New-Item -ItemType Directory -Force "$env:TEMP\mcp-revit-download" | Out-Null
  Invoke-WebRequest "https://github.com/desarrollospaloblanco/mcp-revit/releases/download/<tag>/mcp-revit-<tag>.zip" -OutFile "$env:TEMP\mcp-revit-download\mcp-revit-<tag>.zip"
  Expand-Archive "$env:TEMP\mcp-revit-download\mcp-revit-<tag>.zip" "$env:LOCALAPPDATA\mcp-revit\<tag>"
Comprueba que dentro existan addin\, server\build\index.js, scripts\install-addin.ps1 y VERSION. Luego borra el zip descargado de la carpeta temporal.
Lee `.claude\skills\instalar-mcp-revit\SKILL.md` de la carpeta extraída: es la referencia oficial. Si contradice este prompt, gana el skill y me avisas.

PASO 4 - Versiones de Revit
Detecta qué versiones hay instaladas revisando `C:\Program Files\Autodesk\Revit 2023`, `2024`, `2025` y `2026`. Pregúntame en cuáles instalar (puedo elegir varias).

PASO 5 - Revit cerrado
Comprueba con `Get-Process Revit -ErrorAction SilentlyContinue`. Si está abierto, pídeme que guarde mi trabajo y lo cierre, y espera mi confirmación antes de seguir.

PASO 6 - Instalar el add-in
Desde la carpeta extraída, por cada versión elegida (sin -Build: el release ya viene compilado):
  powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-addin.ps1 -RevitVersion <año>
El script desbloquea los archivos descargados, respalda lo instalado en %USERPROFILE%\mcp-revit-backups\, copia el add-in a %APPDATA%\Autodesk\Revit\Addins\<año>\, registra los comandos en commandRegistry.json y comprueba que el servidor MCP funcione con el Node de esta máquina (si no, reconstruye su módulo nativo descargándolo de GitHub). Muéstrame el resumen de cada versión.

PASO 7 - Conectar Claude al servidor
Revisa con `claude mcp list` si ya hay un servidor llamado mcp-server-for-revit; si existe, quítalo con `claude mcp remove mcp-server-for-revit -s user`. Luego regístralo a nivel de usuario apuntando a esta versión:
  claude mcp add mcp-server-for-revit -s user -- node "<carpeta extraída>\server\build\index.js"
Pregúntame si también uso Claude Desktop en modo chat. Si digo que sí, agrega o actualiza la misma entrada en %APPDATA%\Claude\claude_desktop_config.json dentro de "mcpServers" (command "node", args con la ruta a index.js), conservando todo lo demás del archivo; haz copia de respaldo antes de editarlo.

PASO 8 - Extractor de PDF (opcional)
Pregúntame si voy a modelar desde planos PDF. Si sí: `python -m pip install -r tools\pdf-to-revit\requirements.txt` desde la carpeta extraída.

PASO 9 - Verificación
Pídeme que abra Revit (una de las versiones instaladas) y cualquier modelo. Avísame que al arrancar Revit puede preguntar por un complemento desconocido: la elección entre "Always Load" y "Load Once" es mía, no la tomes tú. Luego debo pulsar "Revit MCP Switch" en el panel "Revit MCP Plugin" de la pestaña Complementos y cerrar la ventana "Open Server (port N)" que aparece (mientras está abierta, Revit no responde al MCP). Cuando te confirme, desde la carpeta extraída ejecuta:
  node scripts\revit-call.mjs manage_documents "{}" 60
Si responde con la lista de documentos abiertos, la instalación funciona. Cada sesión de Revit toma el primer puerto libre entre 8080 y 8089 y se registra en %APPDATA%\RevitMCP\instances; con varias sesiones abiertas, la herramienta select_revit_instance elige cuál usar. Si no, revisa el log en %APPDATA%\Autodesk\Revit\Addins\<año>\revit_mcp_plugin\Logs\ (ojo: "Failed to create command instance" justo después de un registro exitoso es un mensaje heredado, no un error).

PASO 10 - Cierre
Dime que reinicie Claude para que cargue las herramientas del MCP, y dame un resumen en tabla con: versión instalada, carpeta, versiones de Revit, comandos registrados, cliente(s) de Claude conectados, y cómo actualizar en el futuro (pegar el prompt de actualización).
```

---

## Prompt corto para actualizar (ya instalado)

```text
Actualiza el MCP de Revit de DPB en esta máquina al último release de https://github.com/desarrollospaloblanco/mcp-revit. Háblame en español. No clones ni compiles: todo sale del release. No escribas credenciales por mí, no cierres Revit tú y no borres nada sin preguntarme. Pasos: (1) obtén el tag con `(Invoke-RestMethod https://api.github.com/repos/desarrollospaloblanco/mcp-revit/releases/latest).tag_name` (el repo es público, no hace falta GitHub CLI ni iniciar sesión) y dime qué versión tengo ahora (mira `claude mcp list` y las carpetas en %LOCALAPPDATA%\mcp-revit\); si ya es la última, dímelo y detente. (2) Descarga el zip con `Invoke-WebRequest "https://github.com/desarrollospaloblanco/mcp-revit/releases/download/<tag>/mcp-revit-<tag>.zip" -OutFile "$env:TEMP\mcp-revit-<tag>.zip"` y extráelo en %LOCALAPPDATA%\mcp-revit\<tag>. (3) Comprueba que Revit esté cerrado; si no, pídeme que guarde y lo cierre. (4) Por cada versión de Revit que tenga la carpeta revit_mcp_plugin en %APPDATA%\Autodesk\Revit\Addins\<año>\, corre `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install-addin.ps1 -RevitVersion <año>` desde la carpeta nueva. (5) Vuelve a registrar el MCP apuntando a la carpeta nueva: `claude mcp remove mcp-server-for-revit -s user` y `claude mcp add mcp-server-for-revit -s user -- node "<carpeta nueva>\server\build\index.js"`; si uso Claude Desktop, actualiza también la ruta en claude_desktop_config.json (con respaldo). (6) Dime que abra Revit, pulse "Revit MCP Switch", cierre la ventana "Open Server" y reinicie Claude; verifica con `node scripts\revit-call.mjs manage_documents "{}" 60` desde la carpeta nueva. Al final pregúntame si borro las carpetas de versiones anteriores. Si algo falla, muéstrame el error real.
```

---

## Qué queda instalado

| Componente | Ubicación |
|---|---|
| Release extraído | `%LOCALAPPDATA%\mcp-revit\<tag>` (una carpeta por versión; se pueden borrar las viejas) |
| Add-in de Revit | `%APPDATA%\Autodesk\Revit\Addins\<año>\` (manifest `.addin` + carpeta `revit_mcp_plugin`) |
| Registro de comandos | `...\revit_mcp_plugin\Commands\commandRegistry.json` |
| Servidor MCP | `<release>\server\build\index.js`, registrado en Claude a nivel de usuario |
| Respaldos | `%USERPROFILE%\mcp-revit-backups\` |
| Skills de Claude | `<release>\.claude\skills\` (`instalar-mcp-revit`, `pdf-a-revit`), activas al abrir Claude Code en esa carpeta |

Cada versión va en su propia carpeta porque mientras Claude está abierto el servidor MCP mantiene bloqueado su módulo nativo: sobrescribir la carpeta en uso fallaría.

## Publicar una versión nueva (mantenedores)

Con los cambios ya en `main`, crear y subir un tag. El workflow [Release](../.github/workflows/release.yml) compila Revit 2023 a 2026 y publica `mcp-revit-<tag>.zip`:

```bash
git tag v1.2.0 origin/main
```

```bash
git push origin v1.2.0
```

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| La descarga falla | Red o proxy de la oficina bloquea GitHub. Probar `Invoke-WebRequest https://github.com` y, si falla, revisar el proxy con TI. |
| `release not found` | Aún no hay release publicado. Un mantenedor debe crear el tag (ver arriba). |
| `Revit is running. Close it first` | Revit bloquea las DLL. Guardar, cerrar Revit y repetir. |
| Revit 2023/2024 no carga el plugin | Archivos bloqueados por venir de internet. El script ya corre `Unblock-File`; si se copió a mano, repetir con el script. |
| `npm rebuild better-sqlite3 failed` | No hay binario precompilado para esa versión de Node. Instalar Node LTS (`winget install OpenJS.NodeJS.LTS`) y repetir el paso 6. |
| Claude no ve las herramientas | No se reinició Claude, o `claude mcp list` apunta a `npx` o a otra carpeta. Volver al paso 7. |
| `revit-call.mjs` no responde | No se pulsó "Revit MCP Switch", no hay un modelo abierto o quedó abierta la ventana "Open Server" (cerrarla). El servicio escucha en el primer puerto libre desde `localhost:8080`; con varias sesiones de Revit, `list_revit_instances` las muestra. |
| Una herramienta tarda y no termina | Revit tiene un diálogo abierto esperando respuesta. Atenderlo en Revit; las herramientas nuevas resuelven solas las advertencias y los errores que tienen solución. |
