param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$ProjectRoot = $PSScriptRoot
$ModId = "MerchantBlacklist"
$GameModsDir = "K:\SteamLibrary\steamapps\common\Slay the Spire 2\mods\$ModId"
$GameModsDirAlt = "C:\Users\Administrator\AppData\Roaming\SlayTheSpire2\mods\$ModId"

Write-Host "=== MerchantBlacklist Mod Build ===" -ForegroundColor Cyan
Write-Host "Project: $ProjectRoot"

Write-Host "[1/3] dotnet build..." -ForegroundColor Yellow
dotnet build -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Write-Host "[2/3] Copy to mods directory..." -ForegroundColor Yellow
if (Test-Path $GameModsDir) {
    $TargetDir = $GameModsDir
}
elseif (Test-Path $GameModsDirAlt) {
    $TargetDir = $GameModsDirAlt
}
elseif (Test-Path "K:\SteamLibrary\steamapps\common\Slay the Spire 2\mods") {
    $TargetDir = $GameModsDir
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
}
else {
    $TargetDir = $GameModsDirAlt
    New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
}

Write-Host "  Target: $TargetDir"
$DllPath = Join-Path $ProjectRoot ".godot\mono\temp\bin\$Configuration\$ModId.dll"
if (Test-Path $DllPath) {
    Copy-Item -Path $DllPath -Destination (Join-Path $TargetDir "$ModId.dll") -Force
    Write-Host "  Copied $ModId.dll OK" -ForegroundColor Green
}
else {
    Write-Host "  Warning: $DllPath not found" -ForegroundColor Yellow
}

if (Test-Path (Join-Path $ProjectRoot "mod_manifest.json")) {
    Copy-Item -Path (Join-Path $ProjectRoot "mod_manifest.json") -Destination (Join-Path $TargetDir "mod_manifest.json") -Force
    Write-Host "  Copied mod_manifest.json OK" -ForegroundColor Green
}

$ToReleaseDir = Join-Path $ProjectRoot "torelease"
New-Item -ItemType Directory -Force -Path $ToReleaseDir | Out-Null
if (Test-Path $DllPath) {
    Copy-Item -Path $DllPath -Destination (Join-Path $ToReleaseDir "$ModId.dll") -Force
}
if (Test-Path (Join-Path $ProjectRoot "mod_manifest.json")) {
    Copy-Item -Path (Join-Path $ProjectRoot "mod_manifest.json") -Destination (Join-Path $ToReleaseDir "mod_manifest.json") -Force
}

Write-Host "[3/3] Done. Target: $TargetDir" -ForegroundColor Green
Write-Host "         Also snapshot -> $ToReleaseDir"
Write-Host "Build complete. Please test in game." -ForegroundColor Yellow