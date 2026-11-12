# Builds BG MCP and installs it for Revit 2025 on this computer, ready for mcp_reload (no Revit restart).
#   .\stage.ps1            core into BGMCP\<version>\ (the loader picks the newest), loader only if missing
#   .\stage.ps1 -Loader    also replace the loader (only takes effect when Revit next starts)
#   .\stage.ps1 -Zip       also make dist\BGMCP-<version>.zip
param([switch]$Loader, [switch]$Zip)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
$root = Join-Path $addins 'BGMCP'

dotnet build BGMCP.csproj -c Release -v q -nologo | Out-Host
if ($LASTEXITCODE) { throw 'core build failed' }
$version = ([xml](Get-Content BGMCP.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$target = Join-Path $root $version
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item bin\Release\*.dll, bin\Release\*.pdb $target -Force
Write-Host "core $version -> $target"

$loaderDll = Join-Path $root 'BGMCP.Loader.dll'
if ($Loader -or -not (Test-Path $loaderDll)) {
    dotnet build loader\BGMCP.Loader.csproj -c Release -v q -nologo | Out-Host
    if ($LASTEXITCODE) { throw 'loader build failed' }
    try { Copy-Item loader\bin\Release\BGMCP.Loader.dll, loader\bin\Release\BGMCP.Loader.pdb $root -Force; Write-Host "loader -> $root" }
    catch { Write-Warning "loader is in use by a running Revit; it is replaced next time no Revit runs it" }
}

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>BG MCP</Name>
    <Assembly>BGMCP\BGMCP.Loader.dll</Assembly>
    <AddInId>3e9b6f21-7c4a-4d58-a1f0-8b2c5d9e4a63</AddInId>
    <FullClassName>BarrGazetas.RevitMcp.Loader.LoaderApp</FullClassName>
    <VendorId>BARRGAZETAS</VendorId>
    <VendorDescription>Barr Gazetas</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content (Join-Path $addins 'BGMCP.addin') $manifest -Encoding utf8
Write-Host 'manifest -> loader'

if ($Zip) {
    New-Item -ItemType Directory -Force dist | Out-Null
    $z = "dist\BGMCP-$version.zip"
    if (Test-Path $z) { Remove-Item $z }
    Compress-Archive -Path bin\Release\*.dll, loader\bin\Release\BGMCP.Loader.dll -DestinationPath $z
    Write-Host "zip -> $z"
}
