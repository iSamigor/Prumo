# Installs Prumo to C:\Program Files\Prumo, starts it at logon and puts Process Lasso to sleep
# (stopped and set to not start; NOT uninstalled, so it can be turned back on with -RestoreLasso).
# Run elevated:  powershell -ExecutionPolicy Bypass -File tools\install.ps1
param([switch]$RestoreLasso, [switch]$KeepLasso)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:ProgramFiles 'Prumo'
$lassoTasks = 'Process Lasso Management Console (GUI)', 'Session agent for Process Lasso'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Rode este script como administrador.'
}

if ($RestoreLasso) {
    Set-Service ProcessGovernor -StartupType Automatic
    Start-Service ProcessGovernor
    foreach ($t in $lassoTasks) { Enable-ScheduledTask -TaskName $t | Out-Null }
    Start-Process 'C:\Program Files\Process Lasso\ProcessLasso.exe'
    Write-Host 'Process Lasso religado.'
    return
}

Write-Host '1/4 Compilando...'
dotnet publish "$repo\src\Prumo\Prumo.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o "$repo\publish" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação.' }

Write-Host '2/4 Instalando em' $dest
Get-Process Prumo -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --exit; Start-Sleep 2 }
Get-Process Prumo -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$repo\publish\*" $dest -Recurse -Force

if (-not $KeepLasso) {
    Write-Host '3/4 Desligando o Process Lasso (sem desinstalar)...'
    foreach ($t in $lassoTasks) { Disable-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue | Out-Null }
    Get-Process ProcessLasso, bitsumsessionagent -ErrorAction SilentlyContinue | Stop-Process -Force
    Stop-Service ProcessGovernor -Force -ErrorAction SilentlyContinue
    Set-Service ProcessGovernor -StartupType Disabled
} else { Write-Host '3/4 Process Lasso mantido.' }

Write-Host '4/4 Iniciando o Prumo e registrando a inicialização com o Windows...'
Start-Process "$dest\Prumo.exe" -ArgumentList '--enable-autostart'
Write-Host 'Pronto.'
