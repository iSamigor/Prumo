# Installs Prumo to C:\Program Files\Prumo and starts it at logon. If Process Lasso is installed, it is put
# to sleep (service stopped and disabled, NOT uninstalled; -RestoreLasso turns it back on).
# Run elevated:  powershell -ExecutionPolicy Bypass -File tools\install.ps1 [-GameBoost] [-KeepLasso]
#                powershell -ExecutionPolicy Bypass -File tools\install.ps1 -Uninstall
param([switch]$RestoreLasso, [switch]$KeepLasso, [switch]$GameBoost, [switch]$Uninstall, [switch]$KeepConfig)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $env:ProgramFiles 'Prumo'
$data = Join-Path $env:ProgramData 'Prumo'
$lassoTasks = 'Process Lasso Management Console (GUI)', 'Session agent for Process Lasso'
$hasLasso = [bool](Get-Service ProcessGovernor -ErrorAction SilentlyContinue)
$shortcuts = "$env:PUBLIC\Desktop\Prumo.lnk", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Prumo.lnk"

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Rode este script como administrador.'
}

function Stop-Prumo {
    # --exit lets Prumo put priorities and the power plan back before closing.
    Get-Process Prumo -ErrorAction SilentlyContinue | ForEach-Object { & $_.Path --exit; Start-Sleep 2 }
    Get-Process Prumo -ErrorAction SilentlyContinue | Stop-Process -Force
}

if ($RestoreLasso) {
    if (-not $hasLasso) { throw 'O Process Lasso não está instalado.' }
    Set-Service ProcessGovernor -StartupType Automatic
    Start-Service ProcessGovernor
    foreach ($t in $lassoTasks) { Enable-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue | Out-Null }
    Write-Host 'Process Lasso religado.'
    return
}

if ($Uninstall) {
    Write-Host 'Desinstalando o Prumo...'
    Stop-Prumo
    Unregister-ScheduledTask -TaskName 'Prumo' -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($s in $shortcuts) { Remove-Item $s -Force -ErrorAction SilentlyContinue }
    if (-not $KeepConfig) { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
    Write-Host ('Pronto.' + $(if ($KeepConfig) { " Configuração mantida em $data." } else { '' }))
    return
}

Write-Host '1/4 Compilando...'
dotnet publish "$repo\src\Prumo\Prumo.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -o "$repo\publish" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Falha na compilação.' }

Write-Host '2/4 Instalando em' $dest
Stop-Prumo
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item "$repo\publish\*" $dest -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($s in $shortcuts) {
    $lnk = $shell.CreateShortcut($s)
    $lnk.TargetPath = "$dest\Prumo.exe"
    $lnk.WorkingDirectory = $dest
    $lnk.Save()
}

if ($hasLasso -and -not $KeepLasso) {
    Write-Host '3/4 Desligando o Process Lasso (sem desinstalar; -RestoreLasso religa)...'
    foreach ($t in $lassoTasks) { Disable-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue | Out-Null }
    Get-Process ProcessLasso, bitsumsessionagent -ErrorAction SilentlyContinue | Stop-Process -Force
    Stop-Service ProcessGovernor -Force -ErrorAction SilentlyContinue
    Set-Service ProcessGovernor -StartupType Disabled
} else { Write-Host '3/4 Process Lasso: nada a fazer.' }

Write-Host '4/4 Iniciando o Prumo e registrando a inicialização com o Windows...'
$launchArgs = '--enable-autostart --tray'
if ($GameBoost) { $launchArgs += ' --enable-game-boost' }
Start-Process "$dest\Prumo.exe" -ArgumentList $launchArgs
Write-Host 'Pronto.'
