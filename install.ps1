param([string]$GameDir = 'F:\SteamLibrary\steamapps\common\Farm Together')
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'FarmTogether.exe'))) { throw 'FarmTogether.exe was not found in the selected directory.' }
$payload = Join-Path $PSScriptRoot 'dist'
if (-not (Test-Path -LiteralPath $payload)) { $payload = $PSScriptRoot }
$loader = Join-Path $GameDir 'winhttp.dll'
if (Test-Path -LiteralPath $loader) {
    if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) { throw 'Another loader already uses winhttp.dll. Install the plugin manually after checking compatibility.' }
    Write-Host 'Existing BepInEx loader retained.'
} else {
    foreach ($name in @('winhttp.dll','doorstop_config.ini','.doorstop_version')) {
        Copy-Item -LiteralPath (Join-Path $payload $name) -Destination (Join-Path $GameDir $name)
    }
    $targetCore = Join-Path $GameDir 'BepInEx\core'
    New-Item -ItemType Directory -Path $targetCore -Force | Out-Null
    Copy-Item -Path (Join-Path $payload 'BepInEx\core\*') -Destination $targetCore -Recurse -Force
}
$targetPlugin = Join-Path $GameDir 'BepInEx\plugins\FarmProgress'
New-Item -ItemType Directory -Path $targetPlugin -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $payload 'BepInEx\plugins\FarmProgress\FarmProgress.dll') -Destination $targetPlugin -Force
Write-Host 'Farm Progress installed. Restart Farm Together; F8 opens panel, F9 exports report.'
