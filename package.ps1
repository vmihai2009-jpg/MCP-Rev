# Builds BG MCP and makes the release zip, dist\BGMCP-x.y.z.zip: the zip install.cmd comes in and the
# Update button installs from. Raise <Version> in BGMCP.csproj first: the Updater only offers newer versions.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

dotnet build (Join-Path $here 'BGMCP.csproj') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
$dll = Join-Path $here 'bin\Release\BGMCP.Revit.dll'
$v = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version
$version = '{0}.{1}.{2}' -f $v.Major, $v.Minor, [Math]::Max(0, $v.Build)

$stage = Join-Path ([IO.Path]::GetTempPath()) ("BGMCP-package-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $stage 'BGMCP') | Out-Null
Copy-Item $dll (Join-Path $stage 'BGMCP')
# what the add-in needs beside it (from 1.5.0: the DWG reader, ACadSharp); never Revit's own assemblies
Get-ChildItem (Join-Path $here 'bin\Release') -Filter *.dll | Where-Object { $_.Name -ne 'BGMCP.Revit.dll' -and $_.Name -notlike 'RevitAPI*' } |
    Copy-Item -Destination (Join-Path $stage 'BGMCP')
foreach ($f in 'BGMCP.addin', 'install.cmd', 'install.ps1', 'uninstall.cmd', 'uninstall.ps1', 'README.md') { Copy-Item (Join-Path $here $f) $stage }

$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$zip = Join-Path $dist "BGMCP-$version.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Made $zip" -ForegroundColor Green
