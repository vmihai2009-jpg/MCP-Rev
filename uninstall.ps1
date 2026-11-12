# Removes Barr Gazetas MCP from Revit 2025 for the current user (every installed version). Settings and the call log are left in place.
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025'
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host 'Revit is running. Close it, then run this again.' -ForegroundColor Yellow
    exit 1
}
Remove-Item (Join-Path $addins 'BGMCP.addin') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $addins 'BGMCP') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'Barr Gazetas MCP removed. Settings stay in %AppData%\BarrGazetas\MCP, the call log in %LocalAppData%\BarrGazetas\MCP.' -ForegroundColor Green
