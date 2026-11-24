# Packages River Features. Run from PowerShell: tools/package.ps1 -Game "<Vintage Story folder>"
# Builds Release against the game and writes dist/riverfeatures_<version>.zip (version from modinfo.json) with only what
# the game needs: modinfo.json, worldconfig.json, RiverFeatures.dll (+ .pdb), assets/... README.md is NOT packaged: it is
# the repository overview. Copy the zip into the game's Mods folder (do not unpack it).
param(
    [string]$Game = $env:VINTAGE_STORY,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Game) { throw 'Set VINTAGE_STORY or pass -Game <Vintage Story folder>' }

if (-not $NoBuild) {
    & dotnet build (Join-Path $root 'RiverFeatures.csproj') -c Release "-p:VINTAGE_STORY=$Game" -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
}

$version = (Get-Content (Join-Path $root 'modinfo.json') -Raw | ConvertFrom-Json).version
if (-not $version) { throw 'modinfo.json has no version' }

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

$entries = @{
    'modinfo.json'     = Join-Path $root 'modinfo.json'
    'worldconfig.json' = Join-Path $root 'worldconfig.json'
}
Get-ChildItem (Join-Path $root 'assets') -Recurse -File | ForEach-Object {
    $entries[($_.FullName.Substring($root.Length + 1) -replace '\\', '/')] = $_.FullName
}
$bin = Join-Path $root 'bin/Release/net10.0'
$entries['RiverFeatures.dll'] = Join-Path $bin 'RiverFeatures.dll'
if (Test-Path (Join-Path $bin 'RiverFeatures.pdb')) { $entries['RiverFeatures.pdb'] = Join-Path $bin 'RiverFeatures.pdb' }

$zipPath = Join-Path $dist ("riverfeatures_{0}.zip" -f $version)
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, 'Create')
try {
    foreach ($k in ($entries.Keys | Sort-Object)) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$k], $k, 'Optimal') | Out-Null
    }
} finally { $zip.Dispose() }
Write-Host "wrote $zipPath ($($entries.Count) files)"
