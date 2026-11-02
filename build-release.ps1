param([string]$GameDir = 'F:\SteamLibrary\steamapps\common\Farm Together', [string]$Version = '1.6.3')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -GameDir $GameDir
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed' }
$output = Join-Path $PSScriptRoot 'release'
$staging = Join-Path $output ('payload-' + $Version)
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($name in @('winhttp.dll','doorstop_config.ini','.doorstop_version')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "tools\bepinex\$name") -Destination (Join-Path $staging $name) -Force
}
$core = Join-Path $staging 'BepInEx\core'
New-Item -ItemType Directory -Force -Path $core | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'tools\bepinex\BepInEx\core\*.dll') -Destination $core -Force
$plugin = Join-Path $staging 'BepInEx\plugins\FarmProgress'
New-Item -ItemType Directory -Force -Path $plugin | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\FarmProgress\FarmProgress.dll') -Destination $plugin -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "docs\release-$Version.md") -Destination (Join-Path $staging 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'install.ps1') -Destination $staging -Force
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'licenses')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination $plugin -Recurse -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $output "FarmProgress-$Version-win64.zip"
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $package, [IO.Compression.CompressionLevel]::Optimal, $false)
$manifest = [ordered]@{
    version = $Version
    url = "https://github.com/SunRichardSKT/FarmProgress/releases/download/v$Version/FarmProgress-$Version-win64.zip"
    sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $output 'update.json'), ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$sdk = (dotnet --list-sdks | Select-Object -Last 1) -replace ' .*',''
$compiler = Join-Path (Split-Path (Get-Command dotnet).Source) "sdk\$sdk\Roslyn\bincore\csc.dll"
$framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$references = @('mscorlib','System','System.Core','System.Drawing','System.Windows.Forms','System.Web.Extensions','System.IO.Compression','System.IO.Compression.FileSystem') | ForEach-Object { '/reference:' + (Join-Path $framework "$_.dll") }
$updaterSources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'updater') -Filter '*.cs' | ForEach-Object { $_.FullName }
$installer = Join-Path $output "FarmProgress-Setup-$Version.exe"
& dotnet $compiler /nologo /target:winexe /platform:x64 /nostdlib+ /langversion:9 /optimize+ "/out:$installer" @references "/resource:$package,FarmProgress.bundle.zip" @updaterSources
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
try { Copy-Item -LiteralPath $installer -Destination (Join-Path $output 'FarmProgress-Setup.exe') -Force }
catch [System.IO.IOException] { Write-Host 'The unversioned installer is in use; the versioned installer is ready for release.' }
Write-Host "Installer: $installer"
Write-Host "Package: $package"
Write-Host "Manifest: $output\update.json"
