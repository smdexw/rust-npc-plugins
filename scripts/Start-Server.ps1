param([switch]$Background)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$server = Join-Path $root 'server'
$exe = Join-Path $server 'RustDedicated.exe'
if (!(Test-Path $exe)) { throw 'Run Update-Server.cmd first.' }
if (Get-Process RustDedicated -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) { throw 'This server is already running.' }
$cfg = Get-Content (Join-Path $server 'server\npc-dev\cfg\server.cfg') -Raw
$password = [regex]::Match($cfg, 'rcon\.password\s+"([^"]+)"').Groups[1].Value
if ($password -notmatch '^[a-f0-9]{64}$') { throw 'Expected a 64-character hexadecimal RCON password in server.cfg.' }
$argsLine = '-batchmode -nographics +server.identity npc-dev +server.ip 127.0.0.1 +server.port 28015 +server.queryport 28017 +rcon.ip 127.0.0.1 +rcon.port 28016 +rcon.web 1 +rcon.password ' + $password + ' +server.level "Procedural Map" +server.seed 12345 +server.worldsize 1000 +server.maxplayers 5 +server.hostname "NPC Development Local" -logfile logs\server.log'
if ($Background) {
    $p = Start-Process -FilePath $exe -WorkingDirectory $server -ArgumentList $argsLine -WindowStyle Hidden -PassThru
    $p.Id | Set-Content (Join-Path $root 'work\test-server.pid')
    Write-Output "Started server PID $($p.Id)"
} else {
    Start-Process -FilePath $exe -WorkingDirectory $server -ArgumentList $argsLine -NoNewWindow -Wait
}
