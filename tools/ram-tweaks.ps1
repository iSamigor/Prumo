# Windows tweaks that keep background RAM use down without hurting games. Run elevated.
# Every key touched is exported first to tools\ram-tweaks-backup; -Revert imports those exports back.
# Deliberately NOT touched: SysMain, the page file and memory compression (they help games, not hurt).
param([switch]$Revert)
$ErrorActionPreference = 'Continue'   # reg.exe reports missing values on stderr; that is expected
$backup = Join-Path $PSScriptRoot 'ram-tweaks-backup'

# key (reg.exe syntax), value name, DWORD value, what it does
$tweaks = @(
    @('HKLM\SOFTWARE\Policies\Microsoft\Dsh',                     'AllowNewsAndInterests',  0, 'Widgets desligados'),
    @('HKLM\SOFTWARE\Policies\Microsoft\Edge',                    'StartupBoostEnabled',    0, 'Edge não pré-carrega ao ligar o PC'),
    @('HKLM\SOFTWARE\Policies\Microsoft\Edge',                    'BackgroundModeEnabled',  0, 'Edge não fica rodando depois de fechado'),
    @('HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot',  'TurnOffWindowsCopilot',  1, 'Copilot do Windows desligado'),
    @('HKCU\Software\Policies\Microsoft\Windows\WindowsCopilot',  'TurnOffWindowsCopilot',  1, 'Copilot do Windows desligado (usuário)'),
    @('HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy',      'LetAppsRunInBackground', 2, 'Apps da Store não rodam em segundo plano'),
    @('HKCU\Software\Microsoft\Windows\CurrentVersion\CrossDeviceResume\Configuration', 'IsResumeAllowed', 0, '"Continuar de outro dispositivo" desligado')
)

if ($Revert) {
    foreach ($f in Get-ChildItem $backup -Filter *.reg) { reg import $f.FullName 2>$null; Write-Host "restaurado $($f.Name)" }
    foreach ($t in $tweaks) {
        # Values that did not exist before the tweak are deleted again.
        $marker = Join-Path $backup ((($t[0] + '_' + $t[1]) -replace '[\\: ]', '_') + '.absent')
        if (Test-Path $marker) { reg delete $t[0] /v $t[1] /f 2>$null | Out-Null; Write-Host "removido $($t[0])\$($t[1])" }
    }
    return
}

New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($t in $tweaks) {
    $name = ($t[0] + '_' + $t[1]) -replace '[\\: ]', '_'
    $file = Join-Path $backup "$name.reg"
    if (-not (Test-Path $file) -and -not (Test-Path (Join-Path $backup "$name.absent"))) {
        reg query $t[0] /v $t[1] 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { reg export $t[0] $file /y | Out-Null }                     # value existed: keep its key
        else { New-Item -ItemType File (Join-Path $backup "$name.absent") | Out-Null }        # value is new: delete on revert
    }
    reg add $t[0] /v $t[1] /t REG_DWORD /d $t[2] /f | Out-Null
    Write-Host "ok  $($t[3])"
}
Write-Host 'Pronto. Vale depois de reiniciar.'
