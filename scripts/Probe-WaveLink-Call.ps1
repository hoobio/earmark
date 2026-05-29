#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Method,
    [Parameter(Mandatory)][hashtable]$Params,
    [int]$ResponseTimeoutMs = 4000
)

$ErrorActionPreference = 'Stop'

$wsInfoPath = Join-Path $env:LOCALAPPDATA 'Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState\ws-info.json'
$port = (Get-Content $wsInfoPath -Raw | ConvertFrom-Json).port

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.Options.SetRequestHeader('Origin', 'streamdeck://')
$cts = [System.Threading.CancellationTokenSource]::new(5000)
$ws.ConnectAsync([Uri]"ws://127.0.0.1:$port", $cts.Token).Wait()

function Send-Json($Ws, $Object) {
    $json = $Object | ConvertTo-Json -Depth 10 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $seg = [System.ArraySegment[byte]]::new($bytes)
    $Ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).Wait()
}

function Receive-Json($Ws, [int]$TimeoutMs) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    $sb = [System.Text.StringBuilder]::new()
    $buf = [byte[]]::new(16384)
    while ([DateTime]::UtcNow -lt $deadline) {
        $task = $Ws.ReceiveAsync([System.ArraySegment[byte]]::new($buf), [System.Threading.CancellationToken]::None)
        while (-not $task.IsCompleted -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 25 }
        if (-not $task.IsCompleted) { return $null }
        try { $r = $task.Result } catch { return $null }
        $null = $sb.Append([System.Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
        if ($r.EndOfMessage) { return $sb.ToString() }
    }
    return $null
}

$req = @{ jsonrpc = '2.0'; method = $Method; id = 1; params = $Params }
Write-Host "--> $Method" -ForegroundColor Cyan
Write-Host ($req | ConvertTo-Json -Depth 10 -Compress) -ForegroundColor DarkGray
Send-Json -Ws $ws -Object $req

$deadline = [DateTime]::UtcNow.AddMilliseconds($ResponseTimeoutMs)
$resp = $null
while ([DateTime]::UtcNow -lt $deadline -and $null -eq $resp) {
    $msg = Receive-Json -Ws $ws -TimeoutMs 1000
    if ($null -eq $msg) { continue }
    try {
        $obj = $msg | ConvertFrom-Json
        if ($obj.id -eq 1 -or $obj.PSObject.Properties.Name -contains 'error') { $resp = $obj; break }
    } catch {}
}

if ($null -eq $resp) {
    Write-Host "(no response in ${ResponseTimeoutMs}ms)" -ForegroundColor Yellow
} else {
    $resp | ConvertTo-Json -Depth 10
}

$ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'bye', [System.Threading.CancellationToken]::None).Wait(500) | Out-Null
$ws.Dispose()
