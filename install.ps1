# Installs Barr Gazetas MCP for Revit 2025, for the current Windows user only (no admin rights).
# Easiest: double-click install.cmd. Needed once: after that, the Update button in Revit installs new releases.
# - From the release zip: copies the ready-built files next to this script.
# - From the source folder: builds first (needs the .NET 8 SDK and Revit 2025 installed).
# Each version goes into a folder of its own (BGMCP\1.2.0\), the same layout the Update button uses.
$ErrorActionPreference = 'Stop'
$here   = $PSScriptRoot
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$root   = Join-Path $addins 'BGMCP'

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host 'Revit is running. Close it and run this again, or use the Update button in Revit (Barr Gazetas tab > MCP > Update).' -ForegroundColor Yellow
    exit 1
}

$packaged = Join-Path $here 'BGMCP\BGMCP.Revit.dll'
if (Test-Path $packaged) {
    $dll = $packaged
} else {
    dotnet build (Join-Path $here 'BGMCP.csproj') -c Release -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
    $dll = Join-Path $here 'bin\Release\BGMCP.Revit.dll'
}

$v = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version
$version = '{0}.{1}.{2}' -f $v.Major, $v.Minor, [Math]::Max(0, $v.Build)
$target = Join-Path $root $version

New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item $dll $target -Force
# what the add-in needs beside it (from 1.5.0: the DWG reader, ACadSharp)
Get-ChildItem (Split-Path $dll) -Filter *.dll | Where-Object { $_.Name -ne 'BGMCP.Revit.dll' -and $_.Name -notlike 'RevitAPI*' } | Copy-Item -Destination $target -Force
$pdb = [IO.Path]::ChangeExtension($dll, '.pdb')
if (Test-Path $pdb) { Copy-Item $pdb $target -Force }
# Files from a downloaded zip carry an "internet" mark that can stop Revit loading them.
Get-ChildItem $target | Unblock-File

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BG MCP</Name>
    <Assembly>BGMCP\$version\BGMCP.Revit.dll</Assembly>
    <AddInId>3e9b6f21-7c4a-4d58-a1f0-8b2c5d9e4a63</AddInId>
    <FullClassName>BarrGazetas.RevitMcp.App</FullClassName>
    <VendorId>BARRGAZETAS</VendorId>
    <VendorDescription>Barr Gazetas</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path (Join-Path $addins 'BGMCP.addin') -Value $manifest -Encoding UTF8

# The 1.1.0 layout put the add-in straight in BGMCP\: no longer used.
Remove-Item (Join-Path $root 'BGMCP.Revit.dll'), (Join-Path $root 'BGMCP.Revit.pdb') -Force -ErrorAction SilentlyContinue

Write-Host "Barr Gazetas MCP $version installed to $target" -ForegroundColor Green
Write-Host 'Start Revit 2025 (choose "Always Load" if asked). On the Barr Gazetas tab, MCP panel: click MCP to switch it on, Connect for how to connect Claude.' -ForegroundColor Green
Write-Host 'Later releases: save the zip in Downloads and click Update in Revit.' -ForegroundColor Green
