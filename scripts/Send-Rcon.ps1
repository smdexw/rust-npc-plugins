param([Parameter(Mandatory=$true)][string]$Command)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cfg = Get-Content (Join-Path $root 'server\server\npc-dev\cfg\server.cfg') -Raw
$match = [regex]::Match($cfg, 'rcon\.password\s+"([^"]+)"')
if (!$match.Success) { throw 'RCON password was not found in server.cfg' }
$socket = [System.Net.WebSockets.ClientWebSocket]::new()
$timeout = [System.Threading.CancellationTokenSource]::new(15000)
try {
    $uri = [uri]('ws://127.0.0.1:28016/' + [uri]::EscapeDataString($match.Groups[1].Value))
    $null = $socket.ConnectAsync($uri, $timeout.Token).GetAwaiter().GetResult()
    $json = @{Identifier=42; Message=$Command; Name='LocalDev'} | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $null = $socket.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $timeout.Token).GetAwaiter().GetResult()
    if ($Command -eq 'quit') { Write-Output 'Quit command sent.'; return }
    do {
        $stream = [System.IO.MemoryStream]::new()
        do {
            $buffer = New-Object byte[] 8192
            $response = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $timeout.Token).GetAwaiter().GetResult()
            if ($response.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { throw 'RCON closed the connection.' }
            $stream.Write($buffer,0,$response.Count)
        } until ($response.EndOfMessage)
        $reply = [System.Text.Encoding]::UTF8.GetString($stream.ToArray()) | ConvertFrom-Json
        $stream.Dispose()
        if ($reply.Identifier -eq 42) { Write-Output $reply.Message; break }
    } while ($true)
} finally {
    if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        try { $null = $socket.CloseOutputAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'Done', $timeout.Token).GetAwaiter().GetResult() } catch {}
    }
    $socket.Dispose(); $timeout.Dispose()
}


