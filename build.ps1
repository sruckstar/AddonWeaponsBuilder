<#
  build.ps1 — publish AddonWeapons Builder as a self-contained Windows x64 folder
  (no .NET install needed on the user's machine), optionally zipped and/or wrapped
  in an Inno Setup installer.

    .\build.ps1                 # tests + publish to .\publish\AddonWeaponsBuilder
    .\build.ps1 -Zip            # ... and AddonWeaponsBuilder-<ver>-win-x64.zip
    .\build.ps1 -Installer      # ... and installer\out\AddonWeaponsBuilder-Setup-<ver>.exe (needs Inno Setup 6)
    .\build.ps1 -SkipTests
#>
param(
    [switch]$Zip,
    [switch]$Installer,
    [switch]$SkipTests,
    [string]$Runtime = "win-x64"
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$out = Join-Path $PSScriptRoot "publish\AddonWeaponsBuilder"
[xml]$props = Get-Content (Join-Path $PSScriptRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version

# tests\ is developer-only and not in the repository — skip when absent
if (-not $SkipTests -and (Test-Path tests\Awb.Tests)) {
    dotnet test tests\Awb.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @("-c", "Release", "-r", $Runtime, "--self-contained", "true", "-o", $out,
            "-p:DebugType=none", "-p:PublishReadyToRun=true")
dotnet publish src\Awb.App @common
if ($LASTEXITCODE -ne 0) { throw "publish (app) failed" }
dotnet publish src\Awb.Cli @common
if ($LASTEXITCODE -ne 0) { throw "publish (cli) failed" }

# native debug symbols shipped inside the SkiaSharp/HarfBuzz packages (~100 MB) are useless to users
Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item -Force

$mb = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("Published {0} -> {1} ({2:N0} MB)" -f $version, $out, $mb)

if ($Zip) {
    $zipPath = Join-Path $PSScriptRoot "publish\AddonWeaponsBuilder-$version-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path "$out\*" -DestinationPath $zipPath
    Write-Host "Zip -> $zipPath"
}

if ($Installer) {
    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
              "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
              "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }
    & $iscc "/DMyAppVersion=$version" installer\AddonWeaponsBuilder.iss
    if ($LASTEXITCODE -ne 0) { throw "installer build failed" }
}
