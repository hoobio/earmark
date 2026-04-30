#Requires -Version 5.1
[CmdletBinding()]
param(
    [string[]]$Methods = @('getMixes', 'getOutputDevices', 'getInputDevices', 'getChannels', 'getApplicationInfo'),
    [int]$ResponseTimeoutMs = 4000
)

$ErrorActionPreference = 'Stop'

$wsInfoPath = Join-Path $env:LOCALAPPDATA 'Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState\ws-info.json'
if (-not (Test-Path $wsInfoPath)) {
    throw "ws-info.json not found at $wsInfoPath. Is Wave Link running?"
}

$port = (Get-Content $wsInfoPath -Raw | ConvertFrom-Json).port
Write-Verbose "Using port $port from $wsInfoPath"

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.Options.SetRequestHeader('Origin', 'streamdeck://')
$cts = [System.Threading.CancellationTokenSource]::new(5000)
$ws.ConnectAsync([Uri]"ws://127.0.0.1:$port", $cts.Token).Wait()
Write-Verbose "Connected (state: $($ws.State))"

function Send-Json {
    param($Ws, $Object)
    $json = $Object | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = [System.ArraySegment[byte]]::new($bytes)
    $Ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).Wait()
}

function Receive-Json {
    param($Ws, [int]$TimeoutMs)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $sb = [System.Text.StringBuilder]::new()
    $buf = [byte[]]::new(8192)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remain = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
        if ($remain -le 0) { break }
        $cts2 = [System.Threading.CancellationTokenSource]::new($remain)
        try {
            $task = $Ws.ReceiveAsync([System.ArraySegment[byte]]::new($buf), $cts2.Token)
            $task.Wait()
            $r = $task.Result
            $null = $sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
            if ($r.EndOfMessage) { return $sb.ToString() }
        } catch {
            return $null
        }
    }
    return $null
}

$id = 1
$results = [ordered]@{}

# Drain any unsolicited push frames first
while ($null -ne (Receive-Json -Ws $ws -TimeoutMs 250)) {}

foreach ($method in $Methods) {
    $req = @{ jsonrpc = '2.0'; method = $method; id = $id }
    Write-Host "--> $method (id=$id)" -ForegroundColor Cyan
    Send-Json -Ws $ws -Object $req

    $resp = $null
    $deadline = [DateTime]::UtcNow.AddMilliseconds($ResponseTimeoutMs)
    while ([DateTime]::UtcNow -lt $deadline -and $null -eq $resp) {
        $msg = Receive-Json -Ws $ws -TimeoutMs 1000
        if ($null -eq $msg) { continue }
        try {
            $obj = $msg | ConvertFrom-Json
            if ($obj.id -eq $id) { $resp = $obj; break }
        } catch {
            Write-Warning "Non-JSON frame: $($msg.Substring(0, [Math]::Min(200, $msg.Length)))"
        }
    }

    if ($null -eq $resp) {
        Write-Host "    (no response in ${ResponseTimeoutMs}ms)" -ForegroundColor Yellow
    } else {
        $results[$method] = $resp
        $resp | ConvertTo-Json -Depth 10
    }
    $id++
}

$ws.CloseOutputAsync('NormalClosure', 'done', [System.Threading.CancellationToken]::None).Wait()

Write-Host "`n=== Summary ===" -ForegroundColor Green
foreach ($m in $results.Keys) {
    $r = $results[$m]
    if ($r.error) {
        Write-Host "$m -> ERROR $($r.error.code): $($r.error.message)" -ForegroundColor Red
    } else {
        Write-Host "$m -> ok" -ForegroundColor Green
    }
}
