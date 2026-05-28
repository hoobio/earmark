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
    # NB: do NOT pass a cancellation token to ReceiveAsync that fires on timeout - cancelling
    # an in-flight receive aborts the whole WebSocket per .NET semantics. Instead, kick off
    # a non-cancellable receive and poll IsCompleted with a deadline.
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $sb = [System.Text.StringBuilder]::new()
    $buf = [byte[]]::new(16384)
    while ([DateTime]::UtcNow -lt $deadline) {
        $task = $Ws.ReceiveAsync([System.ArraySegment[byte]]::new($buf), [System.Threading.CancellationToken]::None)
        while (-not $task.IsCompleted -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 25
        }
        if (-not $task.IsCompleted) { return $null }
        try {
            $r = $task.Result
        } catch {
            return $null
        }
        $null = $sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
        if ($r.EndOfMessage) { return $sb.ToString() }
    }
    return $null
}

$id = 1
$results = [ordered]@{}

# Don't pre-drain: the receive call would block on the first inbound frame and there's no
# safe way to cancel it without aborting the WS. WL's request/response IDs let us
# correlate replies, so any unsolicited push frames in flight get filtered by id matching.

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
