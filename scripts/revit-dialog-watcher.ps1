# Answers Revit dialogs so long MCP scripts are never left blocked by a modal box.
#
#   powershell -File scripts/revit-dialog-watcher.ps1 -TitleMatch "QLE-DI-MJB" [-Log path] [-MaxMinutes 240] [-StopFile path]
#
# Rules (first match wins):
#   "Error - cannot be ignored"  -> Cancel  (rolls back only the transaction that raised it)
#   Revit warnings / messages    -> OK, Close or Continue
#   Recovery "Save As"           -> Cancel  (Revit keeps running; save afterwards)
#   Questions, Open/Save/Print   -> left alone and logged once: those decisions belong to the user
#
# The watcher locks onto ONE Revit process: the one whose window title matches -TitleMatch when it
# starts. Matching by process id afterwards matters because the title changes whenever the user
# switches documents, and a second Revit session (another project) must never be touched.
param(
    [Parameter(Mandatory = $true)][string]$TitleMatch,
    [string]$Log = (Join-Path $env:TEMP "revit-dialog-watcher.log"),
    [int]$MaxMinutes = 240,
    [string]$StopFile = ""
)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

$revit = Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -match [regex]::Escape($TitleMatch) } | Select-Object -First 1
if (-not $revit) { Write-Error "No Revit window title contains '$TitleMatch'"; exit 1 }
$revitPid = $revit.Id
Add-Content $Log ("{0}`tinicio pid={1} ({2})" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $revitPid, $revit.MainWindowTitle)

$NEVER_TOUCH = '^(Open|Abrir|Save As|Guardar como|Print|Imprimir|Export|Exportar|Browse|Select|Seleccionar)'
$QUESTION = 'Do you want|want to save|Save changes|guardar los cambios|Are you sure|Desea|¿'
$seenQuestions = @{}
$attempts = @{}

function Get-DialogText($w) {
    $parts = @()
    foreach ($e in $w.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
        $c = $e.Current
        if ($c.ControlType -eq [System.Windows.Automation.ControlType]::Document) {
            try { $parts += $e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(2000) } catch { }
        } elseif ($c.ControlType -eq [System.Windows.Automation.ControlType]::Text -or $c.ControlType -eq [System.Windows.Automation.ControlType]::Group -or
                  ($c.ControlType -eq [System.Windows.Automation.ControlType]::Pane -and $c.Name -and $c.Name.Length -gt 12)) {
            if ($c.Name) { $parts += $c.Name }
        }
    }
    return (($parts | Select-Object -Unique) -join " | ") -replace "`r?`n", " "
}

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win32Click {
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
"@

# Revit exposes its dialog buttons as Button in some dialogs and as Pane in others (failure dialogs).
function Find-Button($w, [string[]]$names) {
    $types = @([System.Windows.Automation.ControlType]::Button, [System.Windows.Automation.ControlType]::Pane)
    foreach ($n in $names) {
        foreach ($t in $types) {
            $b = $w.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $t)),
                (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $n)))))
            if ($b) { return $b }
        }
    }
    return $null
}

# InvokePattern when the control supports it; otherwise a native BM_CLICK on the button handle,
# and as a last resort WM_COMMAND IDOK/IDCANCEL on the dialog itself.
function Press($dialog, $btn) {
    $name = $btn.Current.Name
    try { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return "invoke" } catch { }
    $h = $btn.Current.NativeWindowHandle
    if ($h -ne 0) { [Win32Click]::PostMessage([IntPtr]$h, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; return "bm_click" }
    $id = if ($name -match '^(Cancel|Cancelar)$') { 2 } else { 1 }
    [Win32Click]::PostMessage([IntPtr]$dialog.Current.NativeWindowHandle, 0x0111, [IntPtr]$id, [IntPtr]::Zero) | Out-Null
    return "wm_command $id"
}

$end = (Get-Date).AddMinutes($MaxMinutes)
$byPid = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $revitPid)
$isDialog = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, "#32770")
while ((Get-Date) -lt $end) {
    if ($StopFile -and (Test-Path $StopFile)) { break }
    if (-not (Get-Process -Id $revitPid -ErrorAction SilentlyContinue)) { break }
    $windows = @($AE::RootElement.FindAll($Scope::Children, $byPid))
    foreach ($top in $windows) {
        # Revit dialogs are #32770 windows: top-level, or nested at any depth under the main window.
        $candidates = @()
        if ($top.Current.ClassName -eq "#32770") { $candidates += $top }
        $candidates += @($top.FindAll($Scope::Descendants, $isDialog))
        foreach ($w in $candidates) {
            $c = $w.Current
            $title = $c.Name
            $text = Get-DialogText $w
            $action = $null; $btn = $null
            if ($title -match 'Save As|Guardar como' -and $text -match 'recover|recuper|serious error|error grave') {
                $btn = Find-Button $w @("Cancel", "Cancelar"); $action = "recuperacion"
            } elseif ($title -match $NEVER_TOUCH) {
                continue
            } elseif ($text -match 'cannot be ignored|no se puede ignorar') {
                $btn = Find-Button $w @("Cancel", "Cancelar", "OK", "Aceptar"); $action = "error"
            } elseif ($text -match $QUESTION) {
                $key = "$title|$text"
                if (-not $seenQuestions.ContainsKey($key)) {
                    $seenQuestions[$key] = 1
                    Add-Content $Log ("{0}`tPREGUNTA (sin responder)`t{1}`t{2}" -f (Get-Date -Format "HH:mm:ss"), $title, $text)
                }
                continue
            } else {
                $btn = Find-Button $w @("OK", "Aceptar", "Close", "Cerrar", "Continue", "Continuar", "Cancel", "Cancelar"); $action = "aviso"
            }
            if ($btn) {
                # Never hammer a dialog that ignores the click: three tries, then report it and move on.
                $hwnd = $c.NativeWindowHandle
                $attempts[$hwnd] = 1 + [int]$attempts[$hwnd]
                if ($attempts[$hwnd] -gt 3) {
                    if ($attempts[$hwnd] -eq 4) { Add-Content $Log ("{0}`tNO SE PUDO CERRAR`t{1}`t{2}" -f (Get-Date -Format "HH:mm:ss"), $title, $text) }
                    continue
                }
                $name = $btn.Current.Name
                try { $how = Press $w $btn }
                catch { Add-Content $Log ("{0}`tFALLO al pulsar {1}`t{2}" -f (Get-Date -Format "HH:mm:ss"), $name, $_.Exception.Message); continue }
                Add-Content $Log ("{0}`t{1}`t{2} ({3})`t{4}`t{5}" -f (Get-Date -Format "HH:mm:ss"), $action, $name, $how, $title, $text)
                Start-Sleep -Milliseconds 700
            }
        }
    }
    Start-Sleep -Milliseconds 1000
}
Add-Content $Log ("{0}`tfin" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
