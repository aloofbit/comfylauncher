<#
    Build-Manifest.ps1 -- pack the addons and patches, and write the manifest
    ComfyLauncher.exe reads.

    Output is a folder that can be copied to the web server as it stands, or
    pointed at directly for a test:

        .\Build-Manifest.ps1
        .\ComfyLauncher.exe --client <a client> --source dist --dry-run

    .\Build-Manifest.ps1 -Local is a local release: the same build, with no
    GitHub release at the end. A launcher that reads the local website takes
    the update from its own update button.

    RUN FROM comfy-wow\launcher, where this repo is cloned. The addons come from
    comfy-wow's addon\ beside it, so this script builds ComfyCraft's manifest
    and nobody else's.

    NOTHING IS LISTED BY HAND. Addons are every folder under addon\ holding a
    .toc of its own name, which is the rule Install-Addon.ps1 already follows
    and the reason a new addon needs nothing edited anywhere. Patches are every
    file under patches\. What IS hand edited is in lists\: the client mods, the
    packages, the realms, the patch names and the addon catalogue.

    A VERSION IS A CONTENT HASH, NOT A NUMBER SOMEBODY REMEMBERS TO BUMP. It is
    computed over the relative paths and the file hashes, so it is the same for
    the same content on any machine, and it changes exactly when the content
    does. A `## Version` in a .toc would be a second thing to keep true, and
    the first time it was forgotten the update would simply not ship.

    The zip's own sha256 goes in the manifest as well, and is a different thing:
    the version answers "is this different from what I have", the sha256 answers
    "did the download arrive intact".
#>

[CmdletBinding()]
param(
    # Where the packed files and manifest.json are written.
    [string] $Out,

    # The addon folders. Defaults to addon\ beside this repo's root.
    [string] $Addons,

    # Files to ship into the client as they are: MPQ patches now, anything with
    # a path later. A note for one goes in <name>.txt beside it.
    [string] $Patches,

    # Where the launcher will fetch them from. Only needed if the files end up
    # somewhere other than beside the manifest.
    [string] $BaseUrl = '',

    # Stop after the tables at the top and build nothing, so a test can read
    # $Supersedes without packing 40 MB of zips. Same switch and same purpose as
    # addon\Install-Addon.ps1's, and it exists because that table has a twin
    # over there which nothing but a test can hold it in step with.
    [switch] $LoadOnly,

    # Keep the launcher's release as it is. Without this, a build whose launcher
    # files differ from the last build raises $LauncherVersion by itself, so
    # installed copies are offered the change. Use it for a change that waits.
    [switch] $Hold,

    # A local release: write dist\ and stop. No commit, no tag, no GitHub
    # release. The local website serves dist\ at 127.0.0.1:8080/launcher, so a
    # client whose launcher reads that source is offered the update.
    [switch] $Local
)

$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Root = Split-Path -Parent $Here

if (-not $Out)     { $Out     = Join-Path $Here 'dist' }
if (-not $Addons)  { $Addons  = Join-Path $Root 'addon' }
if (-not $Patches) { $Patches = Join-Path $Here 'patches' }

# ADDONS THAT HAVE BEEN FOLDED INTO ANOTHER ONE: the surviving addon -> the
# folder names it has taken over. Shipped in the manifest as `supersedes`, and
# the launcher deletes those folders from the client when it installs this one.
#
# Keyed by the SURVIVOR here, and the other way round in Install-Addon.ps1,
# because the two are answering different questions. That script walks a
# client's folders asking "should this still be here"; this one walks the
# addons being packed and asks "what does this one replace". Neither table can
# reach the other -- addon\ has to survive being zipped and handed to somebody
# with no repo around it -- so they are kept in step by hand, and this comment
# is the only thing holding them together.
#
# WHY IT MATTERS: two copies of the same addon both load, and for the shop code
# that is a visible bug -- each copy shrinks GameMenuFrame by a button's height
# behind its own file-local guard, so the Escape menu loses its bottom buttons.
$Supersedes = @{
    # ComfyMute, ComfyCompanions and ComfyQuiet joined on 2026-09-20: a
    # preference can live in the shared folder once it has its own off switch,
    # and the last of them was given one to qualify.
    #
    # THIS LEAVES ONE SHIPPED ADDON. That is not a problem to solve -- the
    # manifest is a list and a list of one is a list -- but it does mean the
    # Addons tab's `ComfyCraft` group draws a single row, so anything that
    # quietly assumed two is now being asked the question for the first time.
    #
    # ComfyBots IS ON THIS LIST AND IT IS NOT A MISTAKE. It was a plain RENAME
    # rather than a merge -- it became ComfyCompanions on 2026-09-17 and had a
    # line of its own here -- and when its survivor was itself swallowed, the
    # list came with it. A superseded name has to name an addon that still
    # EXISTS, because the launcher only draws these rows while walking the
    # manifest's items, and there is no ComfyCompanions item any more. Left
    # where it was, the entry would have gone out with the addon and taken the
    # only mention of ComfyBots with it.
    'ComfyUI' = @('ComfyInvasion', 'ComfyNPC', 'ComfyNoShop',
                  'ComfyMute', 'ComfyCompanions', 'ComfyQuiet', 'ComfyBots')
}

if ($LoadOnly) { return }

function Write-Head([string] $Text) { Write-Host ''; Write-Host "  $Text" -ForegroundColor Cyan }
function Write-Item([string] $Text) { Write-Host "    $Text" }

# A PULL REQUEST MERGED ON GITHUB is not in this folder until it is pulled, and
# a build from here would publish the lists without it. Said, not done: a pull
# could meet edits of your own that are not committed yet.
& {
    $ErrorActionPreference = 'Continue'
    & git -C $Here fetch -q 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return }
    $behind = "$(& git -C $Here rev-list --count 'HEAD..@{u}' 2>$null)".Trim()
    if ($behind -and $behind -ne '0') {
        Write-Host ''
        Write-Host "  GitHub has $behind commit(s) this folder does not. Run git pull first to build with them." -ForegroundColor Yellow
    }
}

function Format-Size([long] $Bytes) {
    if ($Bytes -ge 1048576) { return ('{0:n1} MB' -f ($Bytes / 1048576)) }
    if ($Bytes -ge 1024)    { return ('{0:n0} KB' -f ($Bytes / 1024)) }
    return "$Bytes B"
}

# The content hash: every file's path and hash, in a stable order, hashed. Zip
# metadata is deliberately not in it -- a zip records timestamps, and a fresh
# git checkout rewrites those, which would make every build look like an update.
function Get-ContentVersion([string] $Folder) {
    $sb = New-Object System.Text.StringBuilder
    $files = Get-ChildItem -LiteralPath $Folder -File -Recurse | Sort-Object FullName
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($Folder.Length).TrimStart('\', '/').Replace('\', '/')
        $h   = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
        [void] $sb.Append($rel).Append(':').Append($h).Append("`n")
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha   = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').Substring(0, 12).ToLower()
}

# The same idea over a named set of files rather than a folder: sorted by name,
# each name and hash in turn, hashed. It is the manifest's `launcher.version`,
# which only launchers from before 2026-09-23 read: they computed their own
# version this way. Current ones read `launcher.release` instead.
function Get-FilesVersion([string[]] $Paths) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($f in ($Paths | Sort-Object { [IO.Path]::GetFileName($_) })) {
        $n = [IO.Path]::GetFileName($f)
        $h = (Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash
        [void] $sb.Append($n).Append(':').Append($h).Append("`n")
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($sb.ToString())
    $sha   = [Security.Cryptography.SHA256]::Create()
    return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').Substring(0, 12).ToLower()
}

function Get-FileVersion([string] $Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Substring(0, 12).ToLower()
}

# A BOOLEAN OFF THE .toc, for facts that belong to the addon rather than to
# this script. `## X-Unlisted: 1` is the only one so far: it means BUILT AND
# PUBLISHED, BUT NOT OFFERED. The zip is made and uploaded, so the addon has a
# stable URL to hand to somebody working on the project; what it does not get
# is a manifest ITEM, so the launcher draws no row for it and /play does not
# list it, and it stays out of the drop-in zip.
#
# ComfyHousingDev is the one, and it is 147 MB of Catalogue-tab thumbnails.
# Worth its size to somebody building the world, and nothing to somebody who
# bought a chair from a vendor.
#
# IN THE .toc RATHER THAN A TABLE HERE for the same reason nothing enumerates
# addons by hand (see the head of this file): a list beside an auto-discovering
# build is a list that rots without a symptom. The addon carries its own answer,
# and it survives being zipped and handed to somebody with no repo around it.
#
# WoW itself ignores an unknown `## X-` line, which is what makes this free.
function Get-TocFlag([string] $Toc, [string] $Name) {
    foreach ($line in (Get-Content -LiteralPath $Toc -ErrorAction SilentlyContinue)) {
        if ($line -match ('^\s*##\s*' + [regex]::Escape($Name) + '\s*:\s*(.+)$')) {
            $v = $Matches[1].Trim()
            return ($v -eq '1' -or $v -eq 'true' -or $v -eq 'yes')
        }
    }
    return $false
}

# An addon already carries its own description: `## Notes` is the field the
# client's own addon list shows, so it is the one a player has seen before.
# Falling back to `## Title` beats inventing a second place to write this down.
function Get-TocNote([string] $Toc) {
    $note  = ''
    $title = ''
    foreach ($line in (Get-Content -LiteralPath $Toc -ErrorAction SilentlyContinue)) {
        if ($line -match '^\s*##\s*Notes\s*:\s*(.+)$')  { $note  = $Matches[1].Trim() }
        if ($line -match '^\s*##\s*Title\s*:\s*(.+)$')  { $title = $Matches[1].Trim() }
    }
    if ($note) { return ($note -replace '\|c[0-9a-fA-F]{8}', '' -replace '\|r', '') }
    return ($title -replace '\|c[0-9a-fA-F]{8}', '' -replace '\|r', '')
}

# -------------------------------------------------------------------- start
# The launcher as the last build described it, read before dist\ is cleared,
# so the Launcher section can say whether this build offers players a new one.
$previousLauncher = $null
try {
    $previousLauncher = (Get-Content -LiteralPath (Join-Path $Out 'manifest.json') -Raw -ErrorAction Stop |
                         ConvertFrom-Json).launcher
} catch { }

if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Recurse -Force }
$files = Join-Path $Out 'files'
New-Item -ItemType Directory -Path $files -Force | Out-Null

# BOTH, by name. ZipArchiveMode is in System.IO.Compression, which .NET loads
# only when the first addon is zipped, so a build with no addon folder died at
# the drop-in zip with "Unable to find type" (measured 2026-09-25).
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ArrayList rather than List[object], and that is not a style choice.
#
#   @($list) THROWS "Argument types do not match" AND RETURNS AN EMPTY ARRAY
#   when $list is a Generic.List[object] held in a variable.
#
# Measured on PowerShell 5.1.19041.6456. List[string], ArrayList, a hashtable's
# .Keys and a List RETURNED from a function are all fine -- the last because
# PowerShell unrolls a function's output before @() ever sees it, which is why
# tools\ComfyMenu.ps1 uses List[object] for its frame and never notices. The
# failure is quiet in the worst way: the manifest wrote itself with an empty
# items array and a non-zero exit, and the only clue was the exit code.
$items = New-Object System.Collections.ArrayList

# ------------------------------------------------------------------ addons
Write-Head 'Addons'
$found = @(Get-ChildItem -LiteralPath $Addons -Directory -ErrorAction SilentlyContinue | Where-Object {
    Test-Path (Join-Path $_.FullName ($_.Name + '.toc'))
})
if (@($found).Count -eq 0) { Write-Item "none found in $Addons" }

# Filled from each .toc below. Read again by the drop-in zip and by the
# summary at the end, which prints the URL of anything unlisted -- that printed
# line is how the addon gets handed to somebody, since nothing offers it.
$unlistedNames = @{}
$unlistedUrls  = @{}

# THE SAME ENTRIES AGAIN, KEPT FOR THE MANIFEST, AND THIS IS NOT FOR A READER.
# deploy\Update-Server.ps1 decides whether to upload dist\ at all by hashing
# manifest.json, on the reasoning that every url, sha256, size and version in
# the folder is in that file. Unlisted broke that quietly: with nothing of
# ComfyHousingDev in the manifest, changing it left the fingerprint identical,
# the deploy said "the box already has <hash>" and went on serving last week's
# zip under a URL that still worked. Nothing anywhere said so.
#
# So they go in under a key of their own. `items` stays the list of what is
# OFFERED -- the launcher's rows, /play, the drop-in zip all read that and only
# that -- and `unlisted` is inert, read by nothing, there so the manifest
# describes the whole folder again.
$unlistedItems = New-Object System.Collections.ArrayList

foreach ($addon in $found) {
    $zip = Join-Path $files ($addon.Name + '.zip')
    [System.IO.Compression.ZipFile]::CreateFromDirectory($addon.FullName, $zip, 'Optimal', $true)

    $version = Get-ContentVersion $addon.FullName

    # A CHEAP FINGERPRINT BESIDE THE EXACT ONE: how many files and how many
    # bytes. The launcher uses it to answer "is the copy on disk this build"
    # for a client it has no record of, without reading 150 MB: hashing
    # ComfyHousingDev's 9,282 files takes 22 seconds and counting them takes 370
    # milliseconds. Weaker, and only ever used where the alternative is not
    # knowing at all.
    $srcFiles = @(Get-ChildItem -LiteralPath $addon.FullName -File -Recurse -ErrorAction SilentlyContinue)
    $srcCount = @($srcFiles).Count
    $srcBytes = 0
    foreach ($sf in $srcFiles) { $srcBytes += $sf.Length }
    $sha     = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLower()
    $size    = (Get-Item -LiteralPath $zip).Length
    $toc     = Join-Path $addon.FullName ($addon.Name + '.toc')
    $note    = Get-TocNote $toc
    $unlisted = Get-TocFlag $toc 'X-Unlisted'

    $entry = [ordered]@{
        name    = $addon.Name
        kind    = 'addon'
        version = $version
        note    = $note
        url     = $BaseUrl + 'files/' + $addon.Name + '.zip'
        sha256  = $sha
        size    = $size
        files   = $srcCount
        bytes   = $srcBytes
    }
    # Only when there is something to say. An empty array in every other row
    # would be six lines of nothing in a file people read.
    if ($Supersedes[$addon.Name]) { $entry.supersedes = @($Supersedes[$addon.Name]) }

    # UNLISTED MEANS NO ITEM. The zip above was still built and will still be
    # uploaded, so the URL works; it simply never joins $items, and everything
    # downstream -- the launcher's rows, /play's download list, the drop-in zip
    # -- reads $items. So one `continue` is the whole feature, and there is no
    # flag for a reader to misunderstand later.
    #
    # The entry itself is kept, in $unlistedItems, for the deploy's sake alone;
    # see the comment where that list is declared.
    if ($unlisted) {
        $unlistedNames[$addon.Name] = $true
        $unlistedUrls[$addon.Name]  = $entry.url
        $unlistedItems.Add($entry) > $null
        Write-Item ("{0,-18} {1}  {2}  unlisted" -f $addon.Name, $version, (Format-Size $size))
        continue
    }

    $items.Add($entry) > $null

    Write-Item ("{0,-18} {1}  {2}" -f $addon.Name, $version, (Format-Size $size))
}

# ----------------------------------------------------------------- patches
Write-Head 'Patches and client files'
$patchFiles = @(Get-ChildItem -LiteralPath $Patches -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Extension -ne '.txt' -and $_.Name -ne '.gitkeep' })
if (@($patchFiles).Count -eq 0) { Write-Item "none found in $Patches" }

# A SIDECAR WITH NO PATCH BESIDE IT STOPS THE BUILD. patch-V.mpq is not
# tracked, since build-patch-v.js rebuilds it from the client, so a fresh clone
# has its sidecar and not the file. Skipping it quietly would publish a manifest
# without a REQUIRED patch, and every launcher would then let the game start
# without it.
foreach ($s in @(Get-ChildItem -LiteralPath $Patches -Filter '*.txt' -File -ErrorAction SilentlyContinue)) {
    if ($s.Name -eq 'README.txt') { continue }
    $for = Join-Path $Patches ([IO.Path]::GetFileNameWithoutExtension($s.Name))
    if (-not (Test-Path -LiteralPath $for)) {
        throw "$($s.Name) has no patch beside it. Build it first, from comfy-wow's root: node patches/build-patch-v.js for patch-V, node patches/build-reforged-fixes.js for patch-Q and patch-R."
    }
}

$optInPatches = @{}
$packageParts = New-Object System.Collections.ArrayList
foreach ($p in $patchFiles) {
    Copy-Item -LiteralPath $p.FullName -Destination (Join-Path $files $p.Name) -Force

    # `X-OptIn: 1` on a line of its own in the sidecar means OFFERED BUT NEVER
    # PUSHED: the launcher starts the row unticked even on a fresh client, and
    # the drop-in zip leaves it out, since that zip has no row to untick. The
    # line is taken out of the note. patch-V (the quiet login screen) was the
    # first: a player who never asked must still hear the login music. Since
    # 2026-09-23 that letter is the required patch below, and nothing is opt-in.
    #
    # `X-Required: 1` is the other way round: a patch every player must have
    # before they can play. The launcher ticks it, will not untick it, and
    # holds back Start the game until it is installed and current. patch-V is
    # the first, since 2026-09-23: without it the create screen does not offer
    # the new race and class pairs. A sidecar cannot say both.
    #
    # `X-Package: <id>` makes the patch a PART OF THAT PACKAGE in packages.json
    # instead of a row of its own: it is hosted in files\ like any patch, gets
    # no manifest item, stays out of the drop-in zip, and the Packages block
    # below adds it to the package's parts with its size and hash. The Reforged
    # fixes were the first, 2026-10-06: patch-Q rides with Reforged World and patch-R
    # with Characters and gear, because each holds fixed copies of that pack's
    # models and must never reach a client without it.
    $note = ''
    $optIn = $false
    $required = $false
    $package = ''
    $side = Join-Path $Patches ($p.Name + '.txt')
    if (Test-Path -LiteralPath $side) {
        $kept = @()
        foreach ($line in (Get-Content -LiteralPath $side)) {
            if     ("$line" -match '^\s*X-OptIn:\s*1\s*$')    { $optIn = $true }
            elseif ("$line" -match '^\s*X-Required:\s*1\s*$') { $required = $true }
            elseif ("$line" -match '^\s*X-Package:\s*(\S+)\s*$') { $package = $Matches[1] }
            else   { $kept += $line }
        }
        $note = ($kept -join "`n").Trim()
    }
    if ($package) {
        if ($optIn -or $required) { throw "$($p.Name).txt says X-Package with X-OptIn or X-Required" }
        if ($p.Name -notmatch '^patch-([A-Za-z])\.mpq$') { throw "$($p.Name).txt says X-Package, but only a patch-<letter>.mpq can be a package part" }
        $packageParts.Add([pscustomobject]@{
            package = $package
            letter  = $Matches[1].ToUpper()
            size    = $p.Length
            url     = $BaseUrl + 'files/' + $p.Name
            sha256  = (Get-FileHash -LiteralPath $p.FullName -Algorithm SHA256).Hash.ToLower()
            file    = $p.Name
        }) > $null
        Write-Item ("{0,-18} {1}  part of {2}" -f $p.Name, (Format-Size $p.Length), $package)
        continue
    }
    if ($optIn -and $required) { throw "$($p.Name).txt says both X-OptIn and X-Required" }
    if ($optIn) { $optInPatches[$p.Name] = $true }

    # An MPQ goes to Data\ ; anything else keeps the path it was given in its
    # sidecar's first line, which is what makes a future client mod need no new
    # code here. Data\ is the only default because it is the only one so far.
    $target = 'Data/' + $p.Name

    $items.Add([ordered]@{
        name    = [IO.Path]::GetFileNameWithoutExtension($p.Name)
        kind    = 'file'
        path    = $target
        version = (Get-FileVersion $p.FullName)
        note    = $note
        optin   = $optIn
        required = $required
        url     = $BaseUrl + 'files/' + $p.Name
        sha256  = (Get-FileHash -LiteralPath $p.FullName -Algorithm SHA256).Hash.ToLower()
        size    = $p.Length
    }) > $null

    $optLabel = ''
    if ($optIn) { $optLabel = '  opt-in' }
    if ($required) { $optLabel = '  required' }
    Write-Item ("{0,-18} {1}{2}" -f $p.Name, (Format-Size $p.Length), $optLabel)
}

# ---------------------------------------------------------- the drop-in zip
# THE SAME CONTENT, LAID OUT THE WAY A CLIENT IS, for somebody who would rather
# not run anything: Interface\AddOns\<addon>\ and Data\<patch>.mpq, extracted
# over the WoW folder. It is a dead end by design -- there is no version in it
# and nothing to check against later, so taking this route means doing it again
# by hand next time. That is the trade it exists to offer.
#
# Written entry by entry rather than by staging a folder and zipping that,
# because staging means copying ComfyHousingDev's 9,282 files (151 MB) to disk
# first and the zip is the only thing anybody wants. It is still the most
# expensive thing this script does: the same bytes are compressed twice, once
# into files\ComfyHousingDev.zip for the launcher and once into here.
function Add-Tree($Zip, [string] $Folder, [string] $Prefix) {
    $n = 0
    foreach ($f in (Get-ChildItem -LiteralPath $Folder -File -Recurse | Sort-Object FullName)) {
        $rel = $f.FullName.Substring($Folder.Length).TrimStart('\', '/').Replace('\', '/')
        [void] [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $Zip, $f.FullName, ($Prefix + '/' + $rel), 'Optimal')
        $n++
    }
    return $n
}

Write-Head 'Drop-in zip'

# AN OPTIONAL ADDON IS NOT IN THE DROP-IN ZIP. This zip is the "give me
# everything" route for somebody who is not running the launcher, and
# everything has to mean the things everybody wants -- 147 MB of catalogue
# thumbnails handed to a player who only wanted their tooltips fixed is the
# opposite of a convenience. It is not in the manifest either, so the only way
# to it is the URL printed at the end of this build.
$bundled = @($found | Where-Object { -not $unlistedNames[$_.Name] })
foreach ($addon in $found) {
    if ($unlistedNames[$addon.Name]) {
        Write-Item ("{0,-18} unlisted, not bundled" -f $addon.Name)
    }
}
$bundledPatches = @($patchFiles | Where-Object { -not $optInPatches[$_.Name] -and -not ($packageParts | Where-Object file -eq $_.Name) })
foreach ($p in $patchFiles) {
    if ($optInPatches[$p.Name]) {
        Write-Item ("{0,-18} opt-in, not bundled" -f $p.Name)
    }
}

$bundlePath = Join-Path $Out 'ComfyCraft.zip'
$bundleStream = [IO.File]::Open($bundlePath, [IO.FileMode]::Create)
$bundleZip = New-Object System.IO.Compression.ZipArchive(
    $bundleStream, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($addon in $bundled) {
        $n = Add-Tree $bundleZip $addon.FullName ('Interface/AddOns/' + $addon.Name)
        Write-Item ("Interface\AddOns\{0,-18} {1} files" -f $addon.Name, $n)
    }
    foreach ($p in $bundledPatches) {
        # The same target the manifest gives the launcher, so the two routes
        # can never put a file in different places.
        [void] [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $bundleZip, $p.FullName, ('Data/' + $p.Name), 'Optimal')
        Write-Item ("Data\{0}" -f $p.Name)
    }
} finally {
    $bundleZip.Dispose()
    $bundleStream.Dispose()
}

# A version for the SET, so the website can say what it is offering and a second
# build of the same content produces the same string. Every item's own version
# is already a content hash, so hashing the list of them is enough.
$sb = New-Object System.Text.StringBuilder
foreach ($it in $items) { [void] $sb.Append($it.name).Append(':').Append($it.version).Append("`n") }
$sha = [Security.Cryptography.SHA256]::Create()
$bundleVersion = ([BitConverter]::ToString(
    $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sb.ToString()))) -replace '-', '').Substring(0, 12).ToLower()
$bundleSize = (Get-Item -LiteralPath $bundlePath).Length
Write-Item ("{0}  {1}" -f $bundleVersion, (Format-Size $bundleSize))

# ------------------------------------------------------------------- mods
# Client mods are mostly TOGGLED rather than downloaded: the DLL is usually
# already in the client and the launcher only edits the dlls.txt line that
# decides whether VanillaFixes injects one. Five of our six ship with a stock
# Turtle client, so that is the common case and it costs nothing.
#
# WHAT IS NEW IS THE OTHER CASE. An entry may carry a url, and then a client
# that does NOT have the file can fetch it. NOTHING IS REHOSTED AND NOTHING IS
# REPACKED: the url is the upstream project's own release asset and it goes
# into the manifest exactly as written, so this build step copies it and never
# downloads it. That keeps the manifest honest about where a DLL came from, and
# it keeps 9 MB of other people's binaries out of dist\.
#
# The sha256 travels with it, which is the whole safety story: a moved tag or a
# replaced asset fails the check and the launcher installs nothing.
#
# The entry carrying `_comment` keys is the file's own documentation and is
# skipped, so the JSON needs no separate README to go stale.
Write-Head 'Client mods'
$mods = @()
$modsFile = Join-Path $Here 'lists\mods.json'
if (Test-Path -LiteralPath $modsFile) {
    foreach ($m in (Get-Content -LiteralPath $modsFile -Raw | ConvertFrom-Json)) {
        if (-not $m.file) { continue }
        $entry = [ordered]@{
            name = [string]$m.name
            file = [string]$m.file
            note = [string]$m.note
        }
        # The heading the C# launcher draws the row under. Absent means Game
        # fixes. The .ps1 launcher does not read it.
        if ($m.section) { $entry.section = [string]$m.section }
        # Only when there is one. An entry with no url is a switch and nothing
        # else, and writing empty keys beside it would have every mod look
        # fetchable to anything reading the manifest.
        if ($m.url) {
            $entry.version = [string]$m.version
            $entry.from    = [string]$m.from
            $entry.url     = [string]$m.url
            $entry.sha256  = [string]$m.sha256
            # Absent means true, so only the odd one out says anything. Written
            # as a real boolean rather than left implied, because the launcher
            # decides whether to touch dlls.txt by it.
            if ($m.PSObject.Properties['inject'] -and -not $m.inject) { $entry.inject = $false }
            # ConvertTo-Json renders a PSCustomObject's properties in order, so
            # the extract map passes through as it was written.
            if ($m.extract) { $entry.extract = $m.extract }
            # More downloads that come with the mod (an addon in a repo of
            # its own), each with url, sha256 and extract, and off_with_mod for
            # an addon that goes aside when the mod is off. The C# launcher
            # reads it; the .ps1 launcher does not.
            if ($m.also) { $entry.also = @($m.also) }
            # The files and add-on folders this mod took the place of (2026-10-06: comfyatmos.dll for comfyfog.dll and
            # comfygrass.dll). The C# launcher takes the old DLLs out of dlls.txt and sets the files aside as it
            # installs this one. The .ps1 launcher does not read them.
            if ($m.supersedes_files) { $entry.supersedes_files = @($m.supersedes_files) }
            if ($m.supersedes) { $entry.supersedes = @($m.supersedes) }
            # A variant is another build of a file some other entry already
            # names. file_sha256 is the hash of that FILE as it lands, not of
            # the download, and is how the launcher tells the builds apart.
            if ($m.variant) {
                $entry.variant     = $true
                $entry.file_sha256 = [string]$m.file_sha256
                # What the C# launcher's < choice > says for this build. The
                # .ps1 launcher does not read it.
                if ($m.choice) { $entry.choice = [string]$m.choice }
            }
            # THE SAME HASH ANSWERS A SECOND QUESTION for a plain entry: is the
            # copy in the client the build this pin names? Without it the
            # launcher can only compare versions, which it knows for a mod it
            # installed itself and not for one somebody copied in.
            elseif ($m.file_sha256) {
                $entry.file_sha256 = [string]$m.file_sha256
            }
        }
        $mods += $entry
        Write-Item ("{0,-18} {1,-28} {2}" -f $m.name, $m.file,
                    $(if (-not $m.url) { 'switch only' }
                      elseif ($entry.Contains('inject')) { 'fetch only  ' + $m.version }
                      elseif ($entry.Contains('variant')) { 'variant     ' + $m.version }
                      else { 'fetchable   ' + $m.version }))
    }
}
if (@($mods).Count -eq 0) { Write-Item "none listed in $modsFile" }

# ------------------------------------------------------------------ realms
# The realms every copy knows about before anybody adds one. Same
# `Name | realmlist | patchlist` format Launcher.cs uses, so the file can be
# pasted from an existing realmlists.txt.
Write-Head 'Realms'
$realms = @()
$realmsFile = Join-Path $Here 'lists\realms.txt'
foreach ($line in (Get-Content -LiteralPath $realmsFile -ErrorAction SilentlyContinue)) {
    $t = "$line".Trim()
    if (-not $t -or $t.StartsWith('#')) { continue }
    $parts = $t.Split('|')
    if (@($parts).Count -lt 2) { continue }
    $name  = $parts[0].Trim()
    $realm = $parts[1].Trim()
    if (-not $realm) { continue }
    $patch = $realm
    if (@($parts).Count -ge 3 -and $parts[2].Trim()) { $patch = $parts[2].Trim() }
    if (-not $name) { $name = $realm }
    $realms += [ordered]@{ name = $name; realm = $realm; patch = $patch }
    Write-Item ("{0,-18} {1}" -f $name, $realm)
}
if (@($realms).Count -eq 0) { Write-Item "none listed in $realmsFile" }

# --------------------------------------------------------------- the launcher
# What a player's own copy compares itself against. The zip is already written
# below; this is its version and where to get it.
#
# THE LAUNCHER IS THE C# PROJECT in app\ since 2026-09-24: `release` is
# its <Version>, and it is what decides whether players are offered this copy.
# Every launcher before it, .ps1 or C#, compares its own number with this one.
# `version` is the old content hash, kept for launchers that predate `release`.
# The SOURCES of the exe, not the exe: a build writes a new module id into it,
# so the exe's own hash would change every time.
$appDir   = Join-Path $Here 'src'
$csproj   = Join-Path $appDir 'ComfyLauncher.csproj'
$selfFiles = @(Get-ChildItem -LiteralPath $appDir -File | Where-Object { $_.Extension -in '.cs', '.csproj', '.ico' } | ForEach-Object { $_.FullName })
$selfVersion = Get-FilesVersion $selfFiles
$release = ''
$m = [regex]::Match([IO.File]::ReadAllText($csproj), '<Version>([^<]+)</Version>')
if ($m.Success) { $release = $m.Groups[1].Value }
if (-not $release) { throw "No <Version> in $csproj, so there is no release to publish." }

Write-Head 'Launcher'

# RAISED HERE, NOT BY HAND. The number was set by hand from 2026-09-23, and a
# launcher change then shipped only when somebody remembered to raise it, which
# was almost never. Now a build whose launcher files differ from the last
# build's, under the same release, raises the last part itself: 0.2 to 0.3,
# 0.9 to 0.10. -Hold keeps the old number for a change that should wait.
#
# The file is rewritten BEFORE the zip is staged below, so the zip carries the
# new number. Its BOM and line endings are kept, and the hash is taken again,
# so the next build over the same files sees no change and raises nothing.
# A number raised by hand already differs from the last build's, so it is left
# alone.
$changed = $previousLauncher -and [string]$previousLauncher.release -eq $release -and
           [string]$previousLauncher.version -and [string]$previousLauncher.version -ne $selfVersion
if ($changed -and -not $Hold) {
    $parts = $release.Split('.')
    $last  = 0
    if (-not [int]::TryParse($parts[-1], [ref] $last)) {
        throw "Cannot raise release '$release': its last part is not a number. Set <Version> in $csproj by hand."
    }
    $parts[-1] = [string]($last + 1)
    $raised = $parts -join '.'

    $bytes  = [IO.File]::ReadAllBytes($csproj)
    $bom    = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text   = [IO.File]::ReadAllText($csproj)
    $text   = [regex]::Replace($text, '<Version>[^<]+</Version>', ('<Version>' + $raised + '</Version>'))
    [IO.File]::WriteAllText($csproj, $text, (New-Object Text.UTF8Encoding($bom)))

    $release     = $raised
    $selfVersion = Get-FilesVersion $selfFiles
    $changed     = $false
}

Write-Item ("release {0}   files {1}" -f $release, $selfVersion)

# COMPILED AFTER THE NUMBER IS RAISED, so the exe carries the release this
# manifest publishes. An exe with an older number would call itself behind and
# replace itself with itself on every start.
& (Join-Path $Here 'Build-Launcher.ps1')

if ($changed) {
    # -Hold. New downloads get these files, and installed launchers keep
    # theirs until the number moves.
    Write-Host ("    held: the launcher changed and release is still {0}, so installed copies keep theirs" -f $release) -ForegroundColor Yellow
} elseif ($previousLauncher -and [string]$previousLauncher.release -and [string]$previousLauncher.release -ne $release) {
    Write-Host ("    new release: {0} to {1}, installed copies will be offered it" -f $previousLauncher.release, $release) -ForegroundColor Green
}

# ------------------------------------------------------------ the reference client
# OPT IN, and deliberately so. Turtle's own content version is not written
# anywhere readable -- every build reports 1.12.1.5875 -- but Data\patch-5.mpq
# is its incremental content patch and its SIZE changes with each release: the
# two clients on this machine differ by six bytes.
#
# The reference is taken from a real client named in reference-client.txt rather
# than typed here, so it cannot rot into a number nobody remembers to update.
# With no such file there is no reference and the launcher makes no claim about
# anybody's client, which is the right answer to a question we cannot answer.
$clientRef = $null
$refFile = Join-Path $Here 'reference-client.txt'
Write-Head 'Reference client'
if (Test-Path -LiteralPath $refFile) {
    $refPath = ''
    foreach ($line in (Get-Content -LiteralPath $refFile -ErrorAction SilentlyContinue)) {
        $t = "$line".Trim()
        if ($t -and -not $t.StartsWith('#')) { $refPath = $t; break }
    }
    $mpq = Join-Path $refPath 'Data\patch-5.mpq'
    if ($refPath -and (Test-Path -LiteralPath $mpq)) {
        $clientRef = [ordered]@{
            file = 'Data/patch-5.mpq'
            size = (Get-Item -LiteralPath $mpq).Length
        }
        Write-Item ("{0}  {1}" -f $refPath, (Format-Size $clientRef.size))
    } else {
        Write-Item "named in reference-client.txt but not found: $refPath"
    }
} else {
    Write-Item "none (write a client path into $refFile to enable the client check)"
}

# ------------------------------------------------------------- patch names
# What each lettered MPQ usually is, so a client's Patches tab reads as words
# rather than letters before anybody has typed anything. A GUESS, because
# nothing identifies an MPQ from the inside; the launcher lets a person override
# any of it and nothing depends on the name.
Write-Head 'Patch names'
$patchNames = [ordered]@{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $Here 'lists\patch-names.txt') -ErrorAction SilentlyContinue)) {
    $t = "$line".Trim()
    if (-not $t -or $t.StartsWith('#')) { continue }
    $parts = $t.Split('|')
    if (@($parts).Count -lt 2) { continue }
    $letter = $parts[0].Trim().ToUpper()
    $name   = $parts[1].Trim()
    if ($letter -and $name) { $patchNames[$letter] = $name }
}
Write-Item ("{0} letters named" -f @($patchNames.Keys).Count)

# ---------------------------------------------------------------- packages
# THE HD PACKS, for the C# launcher. packages.json goes into the manifest as it
# was written; the .ps1 launcher does not read the key. Checked here, because a
# broken package reaches every player at once: a duplicate id, a need or parent
# that names nothing, or a part with no size or no source fails the build.
Write-Head 'Packages'
$packages = $null
$packagesFile = Join-Path $Here 'lists\packages.json'
if (Test-Path -LiteralPath $packagesFile) {
    $packages = Get-Content -LiteralPath $packagesFile -Raw | ConvertFrom-Json
    $ids = @{}
    foreach ($sec in @($packages.sections)) {
        foreach ($pkg in @($sec.packages)) { if ($ids.ContainsKey($pkg.id)) { throw "packages.json: id '$($pkg.id)' is used twice." }; $ids[$pkg.id] = $true }
    }
    # The X-Package patches from the Patches block, added to their package's
    # parts here so that packages.json never holds a size or hash that a
    # rebuild of the patch would make stale.
    foreach ($pp in $packageParts) {
        $pkg = @($packages.sections | ForEach-Object { $_.packages } | Where-Object { $_.id -eq $pp.package })
        if ($pkg.Count -ne 1) { throw "$($pp.file).txt says X-Package: $($pp.package), which is no package in packages.json." }
        $pkg = $pkg[0]
        if ($pkg.choices) { throw "$($pp.file).txt: '$($pp.package)' has choices, and X-Package adds to plain parts only." }
        if (@($pkg.parts | Where-Object { "$($_.letter)" -ieq $pp.letter })) { throw "$($pp.file).txt: '$($pp.package)' already has a patch-$($pp.letter)." }
        $pkg.parts = @($pkg.parts) + [pscustomobject][ordered]@{ letter = $pp.letter; size = $pp.size; url = $pp.url; sha256 = $pp.sha256 }
    }
    $modFiles = @($mods | ForEach-Object { 'mod:' + $_.file })
    $count = 0
    foreach ($sec in @($packages.sections)) {
        foreach ($pkg in @($sec.packages)) {
            $count++
            foreach ($need in @($pkg.needs | Where-Object { $_ })) {
                if (-not $ids.ContainsKey($need) -and -not ($modFiles | Where-Object { $_ -ieq $need })) { throw "packages.json: '$($pkg.id)' needs '$need', which is no package and no mod." }
            }
            if ($pkg.parent -and -not $ids.ContainsKey($pkg.parent)) { throw "packages.json: '$($pkg.id)' has parent '$($pkg.parent)', which is no package." }
            # patch:<letter> names a player's own patch, which the launcher
            # refuses to tick past (comfylogin and the old patch-Y).
            foreach ($c in @($pkg.conflicts | Where-Object { $_ })) { if (-not $ids.ContainsKey($c) -and $c -notmatch '^patch:[A-Za-z0-9]$') { throw "packages.json: '$($pkg.id)' conflicts with '$c', which is no package and no patch:<letter>." } }
            $lists = @()
            if ($pkg.choices) { foreach ($ch in @($pkg.choices)) { $lists += ,@($ch.parts) } } else { $lists += ,@($pkg.parts) }
            foreach ($list in $lists) {
                if (@($list).Count -eq 0) { throw "packages.json: '$($pkg.id)' has a choice or a package with no parts." }
                foreach ($part in @($list)) {
                    if ("$($part.letter)" -notmatch '^[A-Za-z]$') { throw "packages.json: '$($pkg.id)' has a part with no patch letter." }
                    if ([long]$part.size -le 0) { throw "packages.json: '$($pkg.id)' patch-$($part.letter) has no size." }
                    if (-not $part.url -and -not ($sec._zip -and $null -ne $part.zip)) { throw "packages.json: '$($pkg.id)' patch-$($part.letter) has no url and no zip offset." }
                }
            }
        }
    }
    Write-Item ("{0} packages in {1} sections" -f $count, @($packages.sections).Count)
    # THE DXVK ROW: every build needs the hash of its file, which is how it is
    # recognised, and a build that can be fetched needs all of its source.
    if ($packages.dxvk) {
        if (-not $packages.dxvk.file) { throw "packages.json: dxvk has no file." }
        foreach ($b in @($packages.dxvk.builds)) {
            if ("$($b.sha256)" -notmatch '^[0-9a-fA-F]{64}$') { throw "packages.json: dxvk build '$($b.name)' has no sha256." }
            if ($b.url -and (-not $b.extract -or [long]$b.size -le 0 -or "$($b.archive_sha256)" -notmatch '^[0-9a-fA-F]{64}$')) {
                throw "packages.json: dxvk build '$($b.name)' has a url but no extract, size or archive_sha256."
            }
        }
        Write-Item ("DXVK: {0}" -f ((@($packages.dxvk.builds) | ForEach-Object { $_.name }) -join ', '))
    }
} else {
    Write-Item "none: no $packagesFile"
}

# ---------------------------------------------------------------- catalog
# THE ADDONS A PLAYER CAN FIND BY NAME, for the C# launcher's "Find an addon".
# launcher\addons.json is the owner's list (940 addons on 2026-09-24, most from
# the Turtle wiki), and it carries scrape fields nothing reads. What ships is
# trimmed to dist\addons.json, a file of its own that the launcher fetches only
# when the search opens: the manifest is saved into every comfylauncher.json,
# so the catalog does not go in it. The manifest names the file and its hash.
#
# Per entry: a readme pasted in as HTML becomes one line of text; `requires`
# becomes mod ids the launcher can tick; a fork with pin_release becomes a
# tag zip, which is the one case that does not follow the repo's main branch;
# a name used twice gets its owner beside it.
Write-Head 'Addon catalog'
$catalog = $null
$catalogFile = Join-Path $Here 'lists\addons.json'
if (Test-Path -LiteralPath $catalogFile) {
    # Unrolled through ForEach-Object: PowerShell 5.1's ConvertFrom-Json writes
    # a top-level array as ONE object, so @() alone gives a list of one.
    $list = @((Get-Content -LiteralPath $catalogFile -Raw -Encoding UTF8 | ConvertFrom-Json) | ForEach-Object { $_ })
    $dupes = @{}
    foreach ($e in $list) { $k = "$($e.name)".ToLowerInvariant(); $dupes[$k] = 1 + [int]$dupes[$k] }
    $modIds = @{}
    foreach ($m in $mods) { $modIds[[IO.Path]::GetFileNameWithoutExtension($m.file).ToLowerInvariant()] = 'mod:' + $m.file }
    $catalogList = New-Object System.Collections.Generic.List[object]
    foreach ($e in $list) {
        if (-not $e.name -or "$($e.repo)" -notmatch '^https://(github\.com|gitlab\.com|codeberg\.org)/[^/]+/[^/]+/?$') { throw "addons.json: '$($e.name)' has no name or no plain repo link." }
        # A description that is a pasted readme, in HTML or Markdown, is cut
        # down to its words: no tags, badges, link targets or heading marks.
        $note = "$($e.description)" -replace '(?s)<(style|script|head|title)\b[^>]*>.*?</\1>', ' ' -replace '<[^>]+>', ' ' -replace '&nbsp;', ' ' -replace '&amp;', '&'
        $note = $note -replace '!\[[^\]]*\]\([^)]*\)', ' ' -replace '\[([^\]]*)\]\([^)]*\)', '$1' -replace '(?m)^\s*(#{1,6}|>+|[-*])\s+', ' ' -replace '\*\*|`', ''
        # And no dashes, as in all player-facing text: an em, en or spaced
        # hyphen dash becomes a colon. The dashes are built from their code
        # points: this file has no BOM, so Windows PowerShell reads it as ANSI
        # and a literal em dash here would never match.
        $dashes = [string][char]0x2014 + [char]0x2013
        $note = $note -replace '(^|\s)(#{1,6}|>+)(?=\s)', ' ' -replace '\[(Img|Vid)\d+\]', '' -replace ('\s+[' + $dashes + '-]+\s+'), ': ' -replace ('[' + $dashes + ']'), ', '
        $note = ($note -replace '\s+', ' ').Trim()
        if ($note.Length -gt 160) { $cut = $note.LastIndexOf(' ', 160); $note = $note.Substring(0, $(if ($cut -gt 40) { $cut } else { 160 })) }
        $entry = [ordered]@{ name = "$($e.name)"; repo = "$($e.repo)".TrimEnd('/'); category = "$($e.category)"; note = $note; folder = "$($e.folder)" }
        if ($dupes["$($e.name)".ToLowerInvariant()] -gt 1) { $entry.by = ($entry.repo -split '/')[3] }
        $pin = @($e.forks | Where-Object { $_ -and $_.pin_release -and "$($_.repo)" -match '^https://github\.com/[^/]+/[^/]+/?$' }) | Select-Object -First 1
        if ($pin) { $entry.zip = "$($pin.repo)".TrimEnd('/') + "/archive/refs/tags/$($pin.pin_release).zip" }
        $needs = @()
        foreach ($r in @($e.requires | Where-Object { $_ })) {
            $hit = $modIds.Keys | Where-Object { $_.StartsWith("$r".ToLowerInvariant()) } | Select-Object -First 1
            if ($hit) { $needs += $modIds[$hit] } else { Write-Item "  '$($e.name)' requires '$r', which is no mod; left out" }
        }
        if ($needs.Count -gt 0) { $entry.needs = $needs }
        if ($e.recommended) { $entry.recommended = $true }
        if ($e.installable -eq $false) { $entry.installable = $false }
        if ($e.release_downloads -gt 0) { $entry.downloads = [int]$e.release_downloads }
        $catalogList.Add($entry)
    }
    $catalogOut = Join-Path $Out 'addons.json'
    [IO.File]::WriteAllText($catalogOut, (ConvertTo-Json -InputObject $catalogList.ToArray() -Depth 5 -Compress), (New-Object System.Text.UTF8Encoding $false))
    $catalog = [ordered]@{
        url     = 'addons.json'
        version = (Get-FileHash -LiteralPath $catalogOut -Algorithm SHA256).Hash.Substring(0, 12).ToLowerInvariant()
        count   = $catalogList.Count
    }
    Write-Item ("{0} addons, {1} recommended, {2}" -f $catalogList.Count, @($catalogList | Where-Object { $_.recommended }).Count, (Format-Size (Get-Item -LiteralPath $catalogOut).Length))
} else {
    Write-Item "none: no $catalogFile"
}

# ---------------------------------------------------------------- manifest
$manifest = [ordered]@{
    name    = 'ComfyCraft'
    built   = (Get-Date).ToString('yyyy-MM-dd HH:mm')
    updater = 1
    items   = @($items)
    # NOT A SECOND LIST OF ADDONS. Nothing reads this -- not the launcher, not
    # /play, not the drop-in zip -- and adding a reader is the mistake it is
    # here to avoid inviting. It exists so that hashing this one file still
    # answers "has anything in dist\ changed", which is what the deploy asks.
    unlisted = @($unlistedItems)
    mods    = @($mods)
    # The C# launcher's HD packs; see the Packages block above.
    packages = $packages
    # The addon catalog's file and hash; see the Addon catalog block above.
    catalog = $catalog
    realms  = @($realms)
    patchNames = $patchNames
    launcher = [ordered]@{
        release = $release
        version = $selfVersion
        url     = $BaseUrl + 'ComfyLauncher.zip'
        # So the launcher can draw a real bar while replacing itself rather than
        # a bar with no total. Written after the zip exists, below.
        size    = 0
        # The zip's own hash. A launcher from 1.14 on refuses to replace
        # itself with a download that does not match it, or when it is
        # missing. Written after the zip exists, below.
        sha256  = ''
    }
    # The other way in, for the website to offer beside the launcher. Nothing
    # in the launcher reads this.
    bundle   = [ordered]@{
        version = $bundleVersion
        url     = $BaseUrl + 'ComfyCraft.zip'
        size    = $bundleSize
        # What is IN the zip, not what was found: an unlisted addon is built
        # but not bundled, and a count that included it would put a number on
        # the website that the download does not deliver.
        addons  = @($bundled).Count
        patches = @($bundledPatches).Count
    }
    client   = $clientRef
}

$path = Join-Path $Out 'manifest.json'
($manifest | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $path -Encoding UTF8

# What a player downloads, zipped, to be extracted into their WoW folder.
#
# THE EXE ONLY, AT THE TOP OF THE ZIP, since 2026-09-24. Windows "Extract All"
# already makes a folder named after the zip, so a ComfyLauncher\ folder inside
# it gave ComfyLauncher\ComfyLauncher\ComfyLauncher.exe. The exe finds the
# client from up to four folders below it, so either way of extracting works.
# Both updaters find the exe by name and take a flat zip (SelfUpdate.cs, and
# Update-Self in ComfyLauncher.ps1 for the 0.9 to 0.15 copies).
#
# The zip also carried ComfyLauncher.ps1 as a bridge for launchers up to 0.8,
# whose Update-Self copies only the .ps1 and the .cmd out of the zip. No player
# had one, so the bridge was removed. A launcher that old cannot update itself.
$stage = Join-Path ([IO.Path]::GetTempPath()) ('comfylauncher-zip-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $Here 'ComfyLauncher.exe') -Destination (Join-Path $stage 'ComfyLauncher.exe') -Force
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, (Join-Path $Out 'ComfyLauncher.zip'))
Remove-Item -LiteralPath $stage -Recurse -Force

# The zip is written after the manifest object is built, so its size is filled
# in here and the file rewritten. One pass would need the zip first, and the
# zip needs nothing from the manifest, but the ordering reads better this way.
$manifest.launcher.size = (Get-Item -LiteralPath (Join-Path $Out 'ComfyLauncher.zip')).Length
$manifest.launcher.sha256 = (Get-FileHash -LiteralPath (Join-Path $Out 'ComfyLauncher.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
($manifest | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $path -Encoding UTF8

Write-Head 'Written'
Write-Item $path
Write-Item ("{0} items, {1}" -f @($items).Count,
    (Format-Size ((Get-ChildItem -LiteralPath $files -File | Measure-Object Length -Sum).Sum)))
# THE PRINTED URL IS THE ONLY WAY ANYBODY IS GIVEN AN UNLISTED ADDON. It is on
# no page and in no launcher row by design, so if this build does not print it,
# the addon is effectively gone. Hand it to somebody working on the project;
# they unzip it into Interface\AddOns themselves. It IS in the manifest, under
# `unlisted`, which is a fingerprint and not an offer: unlisted is unlisted the
# way a video is, not the way a password is, and anybody reading manifest.json
# could have guessed files\<Name>.zip anyway.
if (@($unlistedNames.Keys).Count -gt 0) {
    Write-Head 'Unlisted, hand out the link'
    foreach ($n in (@($unlistedNames.Keys) | Sort-Object)) {
        Write-Item ("{0,-18} {1}" -f $n, $unlistedUrls[$n])
    }
}

# THE RELEASE on github.com/aloofbit/comfylauncher: a tag and the zip, when the
# launcher's version moved. Last, because it only reads what this build made,
# and a failure there is a yellow line rather than a stop.
Write-Head 'Release'
if ($Local) {
    Write-Item ("local only: release {0} is in {1}, not on GitHub" -f $release, $Out)
} else {
    & (Join-Path $Here 'Publish-Release.ps1') -Zip (Join-Path $Out 'ComfyLauncher.zip')
}

Write-Host ''
Write-Host '  Test it:  launcher\ComfyLauncher.exe --client <a client> --source launcher\dist --dry-run' -ForegroundColor DarkGray
Write-Host ''
