<#
    Snapshot-Client.ps1: record the base client download, the files a player
    has before ComfyLauncher does anything, into lists\base-client.json.
    Build-Manifest.ps1 copies it into the manifest as `base`, and the
    launcher's Check client compares a client with it.

        .\Snapshot-Client.ps1 -Zip C:\Users\me\Downloads\wow-client.zip
        .\Snapshot-Client.ps1 -Client ..\..\wow-clients\fresh-1.18.1 -Skip SuperWoWhook.dll

    -Zip is the clean way: every entry, as downloaded. -Client reads a folder
    and leaves out what a game or a launcher writes there (WTF, WDB, Logs,
    *.log, *.pub, dlls.txt, realmlist.wtf, ComfyLauncher's own files and
    anything the manifest ships). What a launcher put in that the manifest
    does not name goes in -Skip.

    Each file gets its size. An .exe or .dll also gets its SHA-256: the MPQs
    are 9 GB, and a player's Check client reads sizes for those.

    WoW.EXE IS RECORDED WITH THE IDLE BYTE AS IT SHIPS (jl, 7C). The launcher
    changes that one byte (Game.cs, THE IDLE LOGOUT), so a folder it has run
    in has the other one, and the hash is taken with the byte put back.

    THIS IS AN OWNER TOOL. Run it again when Turtle or OctoWoW ships a new
    download, then Build-Manifest.ps1.
#>

[CmdletBinding()]
param(
    [string] $Zip,
    [string] $Client,
    # Paths, relative to the client, a launcher installed and the manifest does not name.
    [string[]] $Skip = @(),
    # What the base is, for the report's header.
    [string] $Name = 'OctoWoW 1.18.1 download'
)

$ErrorActionPreference = 'Stop'
$Here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Out = Join-Path $Here 'lists\base-client.json'
if (-not $Zip -and -not $Client) { throw 'give -Zip or -Client' }

# The idle check, as Game.cs finds it: the jump is the byte after the pattern.
function Reset-IdleByte([byte[]] $b) {
    $found = -1
    for ($i = 0; $i -le $b.Length - 10; $i++) {
        if ($b[$i] -ne 0x05 -or $b[$i+1] -ne 0xC0 -or $b[$i+2] -ne 0x88 -or $b[$i+3] -ne 0xE4 -or
            $b[$i+4] -ne 0xFF -or $b[$i+5] -ne 0x85 -or $b[$i+6] -ne 0xC0) { continue }
        $j = $i + 7
        if (($b[$j] -ne 0x7C -and $b[$j] -ne 0xEB) -or $b[$j+2] -ne 0xE8) { continue }
        if ($found -ge 0) { return }
        $found = $j
    }
    if ($found -ge 0) { $b[$found] = 0x7C }
}

function Get-Sha([byte[]] $b) {
    $h = [Security.Cryptography.SHA256]::Create()
    (($h.ComputeHash($b) | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Get-Entry([string] $path, [long] $size, [scriptblock] $read) {
    $e = [ordered]@{ path = $path; size = $size }
    if ($path -match '\.(exe|dll)$') {
        $b = & $read
        if ($path -eq 'WoW.exe') { Reset-IdleByte $b }
        $e.sha256 = Get-Sha $b
    }
    [pscustomobject]$e
}

$files = @()
if ($Zip) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Zip).Path)
    try {
        foreach ($en in $z.Entries) {
            if ($en.FullName.EndsWith('/')) { continue }
            $read = { $ms = New-Object IO.MemoryStream; $s = $en.Open(); $s.CopyTo($ms); $s.Dispose(); , $ms.ToArray() }.GetNewClosure()
            $files += Get-Entry ($en.FullName -replace '/', '\') $en.Length $read
        }
    } finally { $z.Dispose() }
} else {
    $root = (Resolve-Path $Client).Path.TrimEnd('\')
    # What the manifest ships, so a played-in folder does not record ComfyCraft's own files as base.
    $m = Get-Content (Join-Path $Here 'dist\manifest.json') -Raw | ConvertFrom-Json
    # Folders: the manifest's addons, and what a mod brings along (SuperWoW's SuperAPI).
    $ours = @($m.items | Where-Object { $_.kind -eq 'addon' } | ForEach-Object { "Interface\AddOns\$($_.name)\" }) +
            @($m.unlisted | ForEach-Object { "Interface\AddOns\$($_.name)\" }) +
            @($m.mods | ForEach-Object { $_.also } | Where-Object { $_ } | ForEach-Object { $_.extract.PSObject.Properties.Value } |
              ForEach-Object { ($_ -replace '/', '\').TrimEnd('\') + '\' })
    $ourFiles = $Skip
    # A lettered patch is a mod's or ComfyCraft's; the download's are numbered.
    $played = '^(WTF|WDB|Logs|Screenshots|Cache|ComfyLauncher|mods)\\|\.(log|pub|bak|old|cache)$|^Data\\patch-[A-Za-z]\.mpq|^(dlls\.txt|realmlist\.wtf|comfylauncher\.json|ComfyLauncher\.exe|addons\.json|VMMFix_preferred_monitor\.txt)$'
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $rel = $f.FullName.Substring($root.Length + 1)
        if ($rel -match $played) { continue }
        if ($ourFiles -contains $rel) { continue }
        if ($ours | Where-Object { $rel.StartsWith($_, 'OrdinalIgnoreCase') }) { continue }
        $full = $f.FullName
        $files += Get-Entry $rel $f.Length { , [IO.File]::ReadAllBytes($full) }.GetNewClosure()
    }
}

$doc = [ordered]@{
    name  = $Name
    taken = (Get-Date -Format 'yyyy-MM-dd')
    files = @($files | Sort-Object { $_.path })
}
($doc | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $Out -Encoding UTF8
Write-Host ("  {0} files, {1:N1} GB, in {2}" -f $files.Count, (($files | Measure-Object -Property size -Sum).Sum / 1GB), $Out)
