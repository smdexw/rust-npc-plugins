$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$server = Join-Path $root 'server'
$exe = Join-Path $server 'RustDedicated.exe'
if (Get-Process RustDedicated -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
    throw 'Stop this server with server.save and quit before updating.'
}
& (Join-Path $root 'tools\steamcmd\steamcmd.exe') +force_install_dir $server +login anonymous +app_update 258550 validate +quit
if ($LASTEXITCODE -ne 0) { throw "SteamCMD failed: $LASTEXITCODE" }
if (!(Test-Path $exe)) { throw 'RustDedicated.exe is missing.' }
$release = Invoke-RestMethod 'https://api.github.com/repos/OxideMod/Oxide.Rust/releases/latest'
$asset = $release.assets | Where-Object name -eq 'Oxide.Rust.zip'
if (!$asset) { throw 'Windows Oxide asset was not found.' }
$zip = Join-Path $root 'work\Oxide.Rust.zip'
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
if ($asset.digest -and ('sha256:' + (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()) -ne $asset.digest) { throw 'Oxide checksum mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath $server -Force
$release | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $root 'work\oxide-release.json')
Write-Host "Updated Rust and installed Oxide $($release.tag_name). Check startup before inviting players."

