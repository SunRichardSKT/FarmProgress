param([string]$GameDir = 'F:\SteamLibrary\steamapps\common\Farm Together')
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameDir 'FarmTogether_Data\Managed'
$sdk = (dotnet --list-sdks | Select-Object -Last 1) -replace ' .*',''
$compiler = Join-Path (Split-Path (Get-Command dotnet).Source) "sdk\$sdk\Roslyn\bincore\csc.dll"
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$testOutput = Join-Path $PSScriptRoot 'verification\live-test-bin'
New-Item -ItemType Directory -Force $testOutput | Out-Null
$refs = @('mscorlib','System','System.Core','System.IO.Compression','System.IO.Compression.FileSystem','System.Web.Extensions') | ForEach-Object { '/reference:' + (Join-Path $framework "$_.dll") }
& dotnet $compiler /nologo /target:exe /nostdlib+ /langversion:9 "/out:$testOutput\LiveRulesTests.exe" @refs "/reference:$managed\Newtonsoft.Json.dll" "/reference:$PSScriptRoot\dist\BepInEx\plugins\FarmProgress\FarmProgress.dll" (Join-Path $PSScriptRoot 'verification\LiveRulesTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Live tests compilation failed' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\FarmProgress\FarmProgress.dll') -Destination $testOutput -Force
Copy-Item -LiteralPath (Join-Path $managed 'Newtonsoft.Json.dll') -Destination $testOutput -Force
# Numeric tests are split from the game adapter so they run without loading Unity.
& (Join-Path $testOutput 'LiveRulesTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Live rule tests failed' }
& dotnet $compiler /nologo /target:exe /nostdlib+ /langversion:9 "/out:$testOutput\UpdaterTests.exe" @refs (Join-Path $PSScriptRoot 'updater\UpdaterCore.cs') (Join-Path $PSScriptRoot 'verification\UpdaterTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Updater tests compilation failed' }
& (Join-Path $testOutput 'UpdaterTests.exe') (Join-Path $PSScriptRoot 'verification\updater-fixtures') (Join-Path $testOutput 'FarmProgress.dll') (Join-Path $PSScriptRoot 'tools\bepinex\BepInEx\core\BepInEx.dll')
if ($LASTEXITCODE -ne 0) { throw 'Updater tests failed' }
& dotnet $compiler /nologo /target:exe /nostdlib+ /langversion:9 "/out:$testOutput\AdminNetworkTests.exe" @refs "/reference:$managed\Newtonsoft.Json.dll" (Join-Path $PSScriptRoot 'src\AdminNetwork.cs') (Join-Path $PSScriptRoot 'verification\AdminNetworkTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Admin protocol tests compilation failed' }
& (Join-Path $testOutput 'AdminNetworkTests.exe') (Join-Path $PSScriptRoot ('verification\admin-fixtures-' + [Guid]::NewGuid().ToString('N')))
if ($LASTEXITCODE -ne 0) { throw 'Admin protocol tests failed' }
