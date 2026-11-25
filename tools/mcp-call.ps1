# Small helper: call a BG MCP tool over HTTP, wait for a job, return the parsed result (so big answers stay out of the chat).
param([string]$Tool, [string]$ArgsJson = '{}', [int]$TimeoutSec = 900)
$u = 'http://localhost:48884/mcp'
$h = @{ Accept = 'application/json, text/event-stream' }
$i = Invoke-WebRequest -Uri $u -Method Post -Headers $h -ContentType 'application/json' -UseBasicParsing -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"cli","version":"1"}}}'
$sid = $i.Headers['Mcp-Session-Id']; if ($sid) { $h['Mcp-Session-Id'] = $sid }
Invoke-WebRequest -Uri $u -Method Post -Headers $h -ContentType 'application/json' -UseBasicParsing -Body '{"jsonrpc":"2.0","method":"notifications/initialized"}' | Out-Null
function Call($name, $argsJson) {
  $b = '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"' + $name + '","arguments":' + $argsJson + '}}'
  $r = Invoke-WebRequest -Uri $u -Method Post -Headers $h -ContentType 'application/json' -UseBasicParsing -Body $b -TimeoutSec 120
  $t = ($r.Content | ConvertFrom-Json).result.content[0].text
  return $t
}
$t = Call $Tool $ArgsJson
$o = $t | ConvertFrom-Json
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($o.job -and $o.status -eq 'running' -and $sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
  $t = Call 'job_status' ('{"job":"' + $o.job + '","wait_seconds":40}')
  $o = $t | ConvertFrom-Json
}
$t
