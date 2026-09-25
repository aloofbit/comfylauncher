<#
    Install-Launcher.ps1 -- put a copy of ComfyLauncher inside a client, so it
    can be double-clicked from there.

        .\Install-Launcher.ps1 "C:\Games\TurtleWoW"
        .\Install-Launcher.ps1 "C:\Games\TurtleWoW" -Source dist

    It builds ComfyLauncher.exe from src\ and copies it into
    <client>\ComfyLauncher\, and nothing else. It deletes the
    ComfyLauncher.cmd and ComfyLauncher.ps1 of the old layout there. The
    launcher finds its client by walking up from wherever it sits, so that is
    the whole configuration.

    -Source records a manifest other than the live one in the copy's
    comfylauncher.json, which is the only way to test a DOUBLE-CLICKED copy
    before anything is published. A local path is made absolute first, because
    the copy reads it from its own folder and not from this one.

    THIS IS AN OWNER TOOL. What a player gets is dist\ComfyLauncher.zip, which
    Build-Manifest.ps1 writes: the same exe, extracted into their WoW folder by
    hand.

    STANDS ALONE since 2026-09-25. It used to dot-source the old ComfyLauncher.ps1
    for six of its functions, and that script was retired with the move of this
    folder into its own repo. The two it needed are written out below.
#>

[CmdletBinding()]
param(
    # The client folder. Positional and greedy, so a folder dragged onto
    # Install-Launcher.cmd lands here. With nothing at all it asks.
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]] $Client,

    # A manifest URL or folder for this copy to use instead of the live one.
    [string] $Source,

    # Clear a recorded source, which is how a test copy is turned back into a
    # real one.
    [switch] $Live
)

$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Definition
# The launcher's own default, in src\Loader.cs.
$LiveManifest = 'https://comfycraft.dedyn.io/launcher/manifest.json'

function Write-Head([string] $Text) { Write-Host ''; Write-Host "  $Text" -ForegroundColor Cyan }
function Write-Item([string] $Text) { Write-Host "    $Text" }

# The folder holding WoW.exe, walking up from the path given, the way the
# launcher finds its own client. Nothing when there is none.
function Resolve-Client([string] $Path) {
    if (-not $Path) { return $null }
    $p = $Path.Trim().Trim('"')
    if (-not (Test-Path -LiteralPath $p)) { return $null }
    $p = (Resolve-Path -LiteralPath $p).Path
    while ($p) {
        if (Test-Path -LiteralPath (Join-Path $p 'WoW.exe')) { return $p }
        $parent = Split-Path -Parent $p
        if ($parent -eq $p) { break }
        $p = $parent
    }
    return $null
}

function Select-ClientFolder {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Choose the WoW folder, the one that holds WoW.exe'
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.SelectedPath }
    return $null
}

# First answer wins: what was passed or dragged, then ask.
$client = $null
foreach ($c in @($Client)) {
    $client = Resolve-Client $c
    if ($client) { break }
    Write-Host ''
    Write-Host "  Not a WoW folder: $c" -ForegroundColor Yellow
}

if (-not $client) {
    $picked = Select-ClientFolder
    if ($picked) {
        $client = Resolve-Client $picked
        if (-not $client) {
            Write-Host ''
            Write-Host "  Not a WoW folder: $picked" -ForegroundColor Red
        }
    }
}

if (-not $client) {
    Write-Host ''
    Write-Host '  Nothing to install into.' -ForegroundColor Red
    Write-Host '  Drag your WoW folder onto Install-Launcher.cmd, or name it:'
    Write-Host '    .\Install-Launcher.ps1 "C:\Games\TurtleWoW"'
    exit 1
}

$dest = Join-Path $client 'ComfyLauncher'
New-Item -ItemType Directory -Path $dest -Force | Out-Null

# BUILT HERE, so the copy carries this folder's launcher and not whatever the
# last manifest build left beside it.
& (Join-Path $Here 'Build-Launcher.ps1')

# A running copy cannot be overwritten but can be renamed; its .old is deleted
# by the new exe at its next start, the same as after a self update.
$destExe = Join-Path $dest 'ComfyLauncher.exe'
$destOld = Join-Path $dest 'ComfyLauncher.exe.old'
Remove-Item -LiteralPath $destOld -Force -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $destExe) {
    try { Copy-Item -LiteralPath (Join-Path $Here 'ComfyLauncher.exe') -Destination $destExe -Force -ErrorAction Stop }
    catch { Rename-Item -LiteralPath $destExe -NewName 'ComfyLauncher.exe.old'; Copy-Item -LiteralPath (Join-Path $Here 'ComfyLauncher.exe') -Destination $destExe -Force }
} else {
    Copy-Item -LiteralPath (Join-Path $Here 'ComfyLauncher.exe') -Destination $destExe -Force
}

# The old layout.
foreach ($f in @('ComfyLauncher.cmd', 'ComfyLauncher.ps1', 'source.txt')) {
    Remove-Item -LiteralPath (Join-Path $dest $f) -Force -ErrorAction SilentlyContinue
}

# The source lives in the copy's OWN comfylauncher.json, beside its state. Read
# and merged rather than written fresh, because that file also holds what is
# installed and which realms were added by hand.
$stateFile = Join-Path $dest 'comfylauncher.json'
$doc = $null
if (Test-Path -LiteralPath $stateFile) {
    try { $doc = (Get-Content -LiteralPath $stateFile -Raw) | ConvertFrom-Json } catch { $doc = $null }
}
$value = ''
if ($Source -or $Live) {
    if ($null -eq $doc) { $doc = New-Object psobject }
    if ($Source) {
        $value = $Source
        if ($value -notmatch '^https?://') { $value = (Resolve-Path -LiteralPath $value).Path }
    }
    if ($doc.PSObject.Properties.Name -contains 'source') { $doc.source = $value }
    else { $doc | Add-Member -NotePropertyName source -NotePropertyValue $value }
    ($doc | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $stateFile -Encoding UTF8
}

Write-Head 'Copied to'
Write-Item $dest
Write-Host ''
if ($value)    { Write-Item ("manifest:  " + $value) }
elseif ($Live) { Write-Item  "manifest:  the live one" }
else           { Write-Item  "manifest:  whatever this copy already had" }

# WILL THIS COPY REPLACE ITSELF ON ITS FIRST RUN?
#
# Only when the manifest's release is HIGHER, since 1.14. Until then any
# difference fired it, so a copy NEWER than the manifest's overwrote itself
# with the published one and ran the old code, silently; that happened on
# 2026-09-20. A copy that is older than the manifest still replaces itself.
$mine = ''
$m = [regex]::Match([IO.File]::ReadAllText((Join-Path $Here 'src\ComfyLauncher.csproj')), '<Version>([^<]+)</Version>')
if ($m.Success) { $mine = $m.Groups[1].Value }

$where = $LiveManifest
if ($doc -and $doc.source) { $where = [string]$doc.source }
$theirs = ''
try {
    if ($where -match '^https?://') {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $manifest = Invoke-RestMethod -Uri $where -UseBasicParsing -TimeoutSec 10
    } else {
        $file = $where
        if (Test-Path -LiteralPath $where -PathType Container) { $file = Join-Path $where 'manifest.json' }
        $manifest = (Get-Content -LiteralPath $file -Raw) | ConvertFrom-Json
    }
    $theirs = [string]$manifest.launcher.release
} catch {
    $theirs = ''
}

Write-Host ''
$older = $false
$vMine = $null; $vTheirs = $null
if ([version]::TryParse($mine, [ref] $vMine) -and [version]::TryParse($theirs, [ref] $vTheirs)) { $older = $vTheirs -gt $vMine }
if ($older) {
    Write-Host '  THIS COPY WILL REPLACE ITSELF THE FIRST TIME IT RUNS.' -ForegroundColor Red
    Write-Item "what was just copied in     $mine"
    Write-Item "what the manifest describes $theirs"
    Write-Item "                            $where"
    Write-Host ''
    Write-Host '  The launcher keeps itself current against that manifest, and this' -ForegroundColor Yellow
    Write-Host '  copy is older, so it will put the published copy in and run that.' -ForegroundColor Yellow
    Write-Host '  Build the manifest first, then install again:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host '    .\Build-Manifest.ps1' -ForegroundColor Cyan
} else {
    if (-not $theirs) {
        # Not reachable is not the same as not matching.
        Write-Item 'could not read the manifest, so a self update was not ruled out'
        Write-Host ''
    }
    Write-Host '  Double-click ComfyLauncher.exe in there.' -ForegroundColor Green
}
Write-Host ''
