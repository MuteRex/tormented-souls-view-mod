# Install or remove TSViewMod on Windows (PowerShell 5.1+). Same steps as install.py.
#   install.ps1 [-Uninstall] [-Game "C:\path\to\Tormented Souls"] [-Dll path]
param(
    [switch]$Uninstall,
    [string]$Game,
    [string]$Dll
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 would otherwise write arrays as {"value":[...],"Count":n}.
if ($PSVersionTable.PSVersion.Major -lt 6) { Remove-TypeData System.Array -ErrorAction SilentlyContinue }
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Assembly = 'TSViewMod'

function Test-Game([string]$dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir 'TormentedSouls_Data\Managed\Assembly-CSharp.dll'))
}

# Next to this script, then Steam libraries, then GOG, then ask.
function Find-Game {
    $candidates = @($Here, (Split-Path -Parent $Here))
    try {
        $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        if ($steam) {
            $libraries = @($steam)
            $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
            if (Test-Path -LiteralPath $vdf) {
                foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                    $libraries += $m.Groups[1].Value -replace '\\\\', '\'
                }
            }
            foreach ($lib in $libraries) { $candidates += Join-Path $lib 'steamapps\common\Tormented Souls' }
        }
    } catch { }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games', 'HKLM:\SOFTWARE\GOG.com\Games') {
        if (Test-Path $key) {
            foreach ($g in Get-ChildItem $key) {
                $p = Get-ItemProperty $g.PSPath
                if ($p.gameName -like '*Tormented Souls*' -and $p.path) { $candidates += $p.path }
            }
        }
    }
    foreach ($c in $candidates) { if (Test-Game $c) { return $c } }
    $answer = Read-Host 'Tormented Souls folder (the one with TormentedSouls.exe)'
    return $answer.Trim('"', ' ')
}

function Read-Json([string]$path) { return (Get-Content -LiteralPath $path -Raw -Encoding UTF8) | ConvertFrom-Json }

# Compact UTF-8 without BOM, like the game's own files.
function Write-Json([string]$path, $data) {
    $json = $data | ConvertTo-Json -Depth 10 -Compress
    $tmp = "$path.tmp"
    [System.IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding $false))
    Move-Item -LiteralPath $tmp -Destination $path -Force
}

function Backup-Once([string]$path) {
    $orig = "$path.tsviewmod-orig"
    if (-not (Test-Path -LiteralPath $orig)) { Copy-Item -LiteralPath $path -Destination $orig }
}

if (-not $Game) { $Game = Find-Game }
if (-not (Test-Game $Game)) { throw "not a Tormented Souls install: $Game" }
$data = Join-Path $Game 'TormentedSouls_Data'
$saPath = Join-Path $data 'ScriptingAssemblies.json'
$riPath = Join-Path $data 'RuntimeInitializeOnLoads.json'
$sa = Read-Json $saPath
$ri = Read-Json $riPath

if ($Uninstall) {
    $names = @(); $types = @()
    for ($i = 0; $i -lt $sa.names.Count; $i++) {
        if ($sa.names[$i] -ne "$Assembly.dll") { $names += $sa.names[$i]; $types += $sa.types[$i] }
    }
    $sa.names = $names; $sa.types = $types
    Write-Json $saPath $sa
    $ri.root = @($ri.root | Where-Object { $_.assemblyName -ne $Assembly })
    Write-Json $riPath $ri
    Remove-Item -LiteralPath (Join-Path $data "Managed\$Assembly.dll") -ErrorAction SilentlyContinue
    Write-Host "uninstalled from $Game (TSViewMod.cfg left in place)"
    exit 0
}

if (-not $Dll) {
    foreach ($c in (Join-Path $Here 'TSViewMod.dll'), (Join-Path $Here '..\TSViewMod\bin\Release\net472\TSViewMod.dll')) {
        if (Test-Path -LiteralPath $c) { $Dll = $c; break }
    }
}
if (-not $Dll -or -not (Test-Path -LiteralPath $Dll)) { throw 'TSViewMod.dll not found: build the mod first or pass -Dll' }
Copy-Item -LiteralPath $Dll -Destination (Join-Path $data "Managed\$Assembly.dll") -Force

# Pre-computed map fits; never overwrites the player's own.
foreach ($c in (Join-Path $Here 'TSViewMod_mapcache.txt'), (Join-Path $Here '..\data\TSViewMod_mapcache.txt')) {
    if (Test-Path -LiteralPath $c) {
        $target = Join-Path $data 'TSViewMod_mapcache.txt'
        if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $c -Destination $target }
        break
    }
}

if (@($sa.names) -notcontains "$Assembly.dll") {
    Backup-Once $saPath
    $sa.names = @($sa.names) + "$Assembly.dll"
    $sa.types = @($sa.types) + 16
    Write-Json $saPath $sa
}
if (-not (@($ri.root) | Where-Object { $_.assemblyName -eq $Assembly })) {
    Backup-Once $riPath
    $ri.root = @($ri.root) + [pscustomobject]@{
        assemblyName = $Assembly; nameSpace = 'TSViewMod'; className = 'Loader'
        methodName = 'Init'; loadTypes = 0; isUnityClass = $false
    }
    Write-Json $riPath $ri
}
Write-Host "installed $Assembly.dll into $(Join-Path $data 'Managed')"
