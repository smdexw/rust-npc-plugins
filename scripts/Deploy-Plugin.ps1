param([Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z][A-Za-z0-9_]*$')][string]$Name)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "plugins\src\$Name.cs"
if (!(Test-Path -LiteralPath $source)) { throw "Missing plugin: $source" }
$target = Join-Path $root 'server\oxide\plugins'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Force
Write-Host "Deployed $Name. Check the server console for compilation and loading results."
