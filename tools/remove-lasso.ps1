# Uninstalls Process Lasso and removes its leftovers, keeping its power plan as "Prumo Alto Desempenho",
# and adds Prumo shortcuts (Desktop + Start menu). Run elevated.
$ErrorActionPreference = 'Continue'
$prumo = Join-Path $env:ProgramFiles 'Prumo\Prumo.exe'
$newPlan = 'Prumo Alto Desempenho'

Write-Host '1. Plano de energia'
$bitsum = (powercfg /list | Select-String 'Bitsum Highest Performance') -replace '.*GUID:\s*([0-9a-f-]+).*', '$1'
$existing = (powercfg /list | Select-String $newPlan) -replace '.*GUID:\s*([0-9a-f-]+).*', '$1'
if ($bitsum -and -not $existing) {
    $dup = (powercfg -duplicatescheme $bitsum) -replace '.*GUID:\s*([0-9a-f-]+).*', '$1'
    powercfg -changename $dup $newPlan 'Desempenho máximo (cópia do plano do Process Lasso)'
    $existing = $dup
}
if ($existing) { powercfg -setactive $existing; Write-Host "   ativo: $newPlan ($existing)" }

Write-Host '2. Prumo passa a usar o plano novo'
& $prumo --exit; Start-Sleep 3
$cfgPath = Join-Path $env:ProgramData 'Prumo\config.json'
if (Test-Path $cfgPath) {
    $text = [IO.File]::ReadAllText($cfgPath)
    [IO.File]::WriteAllText($cfgPath, $text.Replace('Bitsum Highest Performance', $newPlan), (New-Object Text.UTF8Encoding $false))
}

Write-Host '3. Desinstalando o Process Lasso'
Get-Process ProcessLasso, ProcessGovernor, bitsumsessionagent, Insights, LogViewer -ErrorAction SilentlyContinue | Stop-Process -Force
$un = 'C:\Program Files\Process Lasso\uninstall.exe'
if (Test-Path $un) {
    Start-Process $un -ArgumentList '/S' -Wait
    # NSIS uninstallers re-launch themselves from %TEMP% (Au_.exe) and return at once: wait for that copy.
    for ($i = 0; $i -lt 60 -and (Get-Process 'Au_*' -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
    Start-Sleep 2
}

Write-Host '4. Limpando rastros'
Stop-Service ProcessGovernor -Force -ErrorAction SilentlyContinue
sc.exe delete ProcessGovernor | Out-Null
foreach ($t in 'Process Lasso Management Console (GUI)', 'Session agent for Process Lasso') {
    Unregister-ScheduledTask -TaskName $t -Confirm:$false -ErrorAction SilentlyContinue
}
$paths = 'C:\Program Files\Process Lasso', "$env:ProgramData\ProcessLasso", "$env:APPDATA\ProcessLasso", "$env:LOCALAPPDATA\ProcessLasso",
         "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Process Lasso", "$env:PUBLIC\Desktop\Process Lasso.lnk",
         "$env:USERPROFILE\Desktop\Process Lasso.lnk"
foreach ($p in $paths) { if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "   removido $p" } }
$keys = 'HKLM:\SOFTWARE\ProcessLasso', 'HKLM:\SOFTWARE\Bitsum', 'HKLM:\SOFTWARE\WOW6432Node\ProcessLasso', 'HKLM:\SOFTWARE\WOW6432Node\Bitsum',
        'HKCU:\Software\ProcessLasso', 'HKCU:\Software\Bitsum',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ProcessLasso'
foreach ($k in $keys) { if (Test-Path $k) { Remove-Item $k -Recurse -Force -ErrorAction SilentlyContinue; Write-Host "   removido $k" } }
foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run') {
    $item = Get-ItemProperty $k -ErrorAction SilentlyContinue
    if ($item) { $item.PSObject.Properties.Name | Where-Object { $_ -match 'Lasso|Bitsum|ProcessGovernor' } | ForEach-Object { Remove-ItemProperty $k -Name $_ } }
}
# The original Bitsum plan, once the copy is active.
$bitsum = (powercfg /list | Select-String 'Bitsum Highest Performance') -replace '.*GUID:\s*([0-9a-f-]+).*', '$1'
if ($bitsum -and $existing) { powercfg -delete $bitsum; Write-Host '   plano Bitsum original removido' }

Write-Host '5. Atalhos do Prumo'
$shell = New-Object -ComObject WScript.Shell
foreach ($lnk in "$env:PUBLIC\Desktop\Prumo.lnk", "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Prumo.lnk") {
    $s = $shell.CreateShortcut($lnk)
    $s.TargetPath = $prumo
    $s.WorkingDirectory = Split-Path $prumo
    $s.IconLocation = "$prumo,0"
    $s.Description = 'Prumo: equilíbrio de processos'
    $s.Save()
    Write-Host "   $lnk"
}

Write-Host '6. Religando o Prumo'
schtasks /run /tn Prumo | Out-Null
Write-Host 'Pronto.'
