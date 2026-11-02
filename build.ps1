param([string]$GameDir = 'F:\SteamLibrary\steamapps\common\Farm Together')
$ErrorActionPreference = 'Stop'
$managed = Join-Path $GameDir 'FarmTogether_Data\Managed'
$sdk = (dotnet --list-sdks | Select-Object -Last 1) -replace ' .*',''
$compiler = Join-Path (Split-Path (Get-Command dotnet).Source) "sdk\$sdk\Roslyn\bincore\csc.dll"
$references = @('mscorlib','System','System.Core','System.Xml','System.Xml.Linq','Assembly-CSharp','MilkstoneUnityExtensions','SteamworksNetworkInterface','Steamworks.NET','Newtonsoft.Json','UnityEngine','UnityEngine.Networking','UnityEngine.CoreModule','UnityEngine.IMGUIModule','UnityEngine.InputModule','UnityEngine.TextRenderingModule','UnityEngine.ImageConversionModule') | ForEach-Object { '/reference:' + (Join-Path $managed "$_.dll") }
$out = Join-Path $PSScriptRoot 'dist\BepInEx\plugins\FarmProgress'
New-Item -ItemType Directory -Force $out | Out-Null
$sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName }
& dotnet $compiler /nologo /target:library /nostdlib+ /langversion:9 /optimize+ "/out:$out\FarmProgress.dll" @references "/reference:$PSScriptRoot\tools\bepinex\BepInEx\core\BepInEx.dll" "/reference:$PSScriptRoot\tools\bepinex\BepInEx\core\0Harmony.dll" "/resource:$PSScriptRoot\src\report.html,FarmProgress.report.html" @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed' }
Write-Host "Built: $out\FarmProgress.dll"
