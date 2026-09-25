<#
    Pin-Mod.ps1 -- move a client mod in lists\mods.json to another GitHub release.

        .\Pin-Mod.ps1 comfygrass v0.3.0-alpha
        .\Pin-Mod.ps1 comfygrass v0.3.0-alpha -DryRun

    The mod is found by its repo name, its file name or its row name. The
    script downloads the release asset, checks it against the SHA-256 GitHub
    publishes for it, hashes the mod's file inside it, and rewrites the entry's
    version, url, sha256 and file_sha256. Nothing else in the file changes.

    A NEW RELEASE REACHES NOBODY UNTIL ITS PIN MOVES, and that is the point:
    every launcher downloads the DLL named here and loads it into WoW, so a
    release goes out only when somebody chose it. This does the tedious half of
    choosing. It does not commit: read the diff, commit, then run
    Build-Manifest.ps1 and deploy.

    REFUSED rather than guessed: a mod that is not on GitHub, an entry with
    more than one url (a mod that brings a second download, such as SuperWoW
    with SuperAPI), a release with several assets and none named like the old
    one (name it with -Asset), a zip whose layout no longer has a path the
    entry's `extract` names, and a download whose hash is not GitHub's.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $Mod,
    [Parameter(Mandatory = $true, Position = 1)] [string] $Tag,

    # The asset to use when the release has several and none is named like the
    # old one.
    [string] $Asset = '',

    # Say what would change, and write nothing.
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

$Here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$File = Join-Path $Here 'lists\mods.json'

function Write-Item([string] $Text, [string] $Colour = 'Gray') { Write-Host "    $Text" -ForegroundColor $Colour }
function Stop-With([string] $Text) { Write-Host ''; Write-Host "  $Text" -ForegroundColor Red; Write-Host ''; exit 1 }

# gh writes progress to stderr, and Windows PowerShell 5.1 turns a native
# command's redirected stderr into errors, so it runs under Continue and is
# judged by its exit code.
function Invoke-Gh {
    $ErrorActionPreference = 'Continue'
    $out = & gh @args 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Out = ($out -join "`n") }
}

function Get-Sha256([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ------------------------------------------------------------------ the entry
$text = [IO.File]::ReadAllText($File)
# By -InputObject, not the pipeline: Windows PowerShell 5.1 sends a JSON
# array down a pipeline as ONE object, and every entry would then be all of
# them at once.
$entries = ConvertFrom-Json -InputObject $text

$want = $Mod.Trim().ToLowerInvariant()
$found = @($entries | Where-Object {
    $repo = if ("$($_.from)" -match '/([^/]+?)/?$') { $Matches[1].ToLowerInvariant() } else { '' }
    $bare = ([IO.Path]::GetFileNameWithoutExtension("$($_.file)")).ToLowerInvariant()
    $repo -eq $want -or $bare -eq $want -or "$($_.file)".ToLowerInvariant() -eq $want -or "$($_.name)".ToLowerInvariant() -eq $want
})
if ($found.Count -eq 0) { Stop-With "No mod in lists\mods.json is called '$Mod'." }
if ($found.Count -gt 1) { Stop-With ("'$Mod' matches " + (($found | ForEach-Object { $_.name }) -join ', ') + '. Use the file name.') }
$entry = $found[0]

if ("$($entry.url)" -notmatch '^https://github\.com/([^/]+)/([^/]+)/releases/download/') {
    Stop-With "$($entry.name) is not a GitHub release download, so it is pinned by hand."
}
$slug = "$($Matches[1])/$($Matches[2])"
$oldName = [Uri]::UnescapeDataString(("$($entry.url)" -split '/')[-1])

# The entry's own text: from the "  {" that opens it to the "  }" that closes
# it, found around its url, which no other entry shares.
$at = $text.IndexOf([string]$entry.url)
$start = $text.LastIndexOf("`n  {", $at)
$end = $text.IndexOf("`n  }", $at)
if ($at -lt 0 -or $start -lt 0 -or $end -lt 0) { Stop-With "Could not find $($entry.name)'s entry in the file's text." }
$span = $text.Substring($start, $end - $start)
if (([regex]::Matches($span, '"url"\s*:')).Count -gt 1) {
    Stop-With "$($entry.name) has more than one url (it brings a second download). Pin it by hand."
}

# ------------------------------------------------------------------ the release
$r = Invoke-Gh api "repos/$slug/releases/tags/$Tag"
if (-not $r.Ok) { Stop-With "No release $Tag on github.com/$slug." }
$release = $r.Out | ConvertFrom-Json
$assets = @($release.assets)
if ($assets.Count -eq 0) { Stop-With "Release $Tag of $slug has no files." }

$pick = $null
if ($Asset) {
    $pick = $assets | Where-Object { $_.name -eq $Asset } | Select-Object -First 1
    if (-not $pick) { Stop-With ("Release $Tag has no file '$Asset'. It has: " + (($assets | ForEach-Object { $_.name }) -join ', ')) }
} elseif ($assets.Count -eq 1) {
    $pick = $assets[0]
} else {
    # Named like the old one, with the old version swapped for the new.
    $old = [string]$entry.version
    $guesses = @($oldName.Replace($old, $Tag), $oldName.Replace($old.TrimStart('v'), $Tag.TrimStart('v')), $oldName) | Select-Object -Unique
    foreach ($g in $guesses) {
        $pick = $assets | Where-Object { $_.name -eq $g } | Select-Object -First 1
        if ($pick) { break }
    }
    if (-not $pick) { Stop-With ("Release $Tag has several files and none is named like $oldName. Name one with -Asset: " + (($assets | ForEach-Object { $_.name }) -join ', ')) }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('pin-mod-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $r = Invoke-Gh release download $Tag --repo $slug --pattern $pick.name --dir $work
    $download = Join-Path $work $pick.name
    if (-not $r.Ok -or -not (Test-Path -LiteralPath $download)) { Stop-With "Could not download $($pick.name): $($r.Out)" }

    $sha = Get-Sha256 $download
    $digest = "$($pick.digest)" -replace '^sha256:', ''
    if ($digest) {
        if ($digest.ToLowerInvariant() -ne $sha) { Stop-With "$($pick.name) does not match the SHA-256 GitHub publishes for it. Nothing was written." }
    } else {
        Write-Host ''
        Write-Host "  GitHub publishes no digest for $($pick.name), so the download is not checked against one." -ForegroundColor Yellow
    }

    # THE FILE THAT LANDS: the mod's DLL, inside the zip or the download itself.
    $fileSha = $sha
    if ($pick.name -match '\.zip$') {
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($download)
        try {
            $names = @($zip.Entries | ForEach-Object { $_.FullName })
            $inside = $null
            if ($entry.extract) {
                foreach ($p in $entry.extract.PSObject.Properties) {
                    $present = if ($p.Name.EndsWith('/')) { @($names | Where-Object { $_.StartsWith($p.Name, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 }
                               else { @($names | Where-Object { $_ -ieq $p.Name }).Count -gt 0 }
                    if (-not $present) { Stop-With "The zip in $Tag has no '$($p.Name)', which the entry's extract names. Its layout changed: pin it by hand." }
                    if ("$($p.Value)" -ieq "$($entry.file)") { $inside = $p.Name }
                }
            }
            if (-not $inside) {
                $hits = @($zip.Entries | Where-Object { $_.Name -ieq "$($entry.file)" })
                if ($hits.Count -ne 1) { Stop-With "The zip in $Tag holds $($hits.Count) files named $($entry.file). Pin it by hand." }
                $inside = $hits[0].FullName
            }
            $e = $zip.Entries | Where-Object { $_.FullName -ieq $inside } | Select-Object -First 1
            $stream = $e.Open()
            try { $fileSha = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant() }
            finally { $stream.Dispose() }
        } finally { $zip.Dispose() }
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# ------------------------------------------------------------------ the rewrite
$new = [ordered]@{ version = $Tag; url = [string]$pick.browser_download_url; sha256 = $sha }
if ($null -ne $entry.file_sha256) { $new.file_sha256 = $fileSha }

# Spliced by position, not with -replace: a replacement string reads $1 and
# $& as its own, and a URL or a hash has no business being parsed.
$changes = @()
foreach ($key in $new.Keys) {
    $m = [regex]::Match($span, '"' + $key + '"\s*:\s*"([^"]*)"')
    if (-not $m.Success) { Stop-With "$($entry.name)'s entry has no $key." }
    $g = $m.Groups[1]
    if ($g.Value -ne $new[$key]) { $changes += [pscustomobject]@{ Key = $key; Old = $g.Value; New = $new[$key] } }
    $span = $span.Substring(0, $g.Index) + $new[$key] + $span.Substring($g.Index + $g.Length)
}

Write-Host ''
Write-Host "  $($entry.name)  $($entry.version) to $Tag" -ForegroundColor Cyan
if ($changes.Count -eq 0) {
    Write-Item 'already pinned there. Nothing to change.' 'DarkGray'
    Write-Host ''
    exit 0
}
foreach ($c in $changes) {
    Write-Item ("{0,-12} {1}" -f $c.Key, $c.Old) 'DarkGray'
    Write-Item ("{0,-12} {1}" -f '', $c.New) 'Green'
}
Write-Item ("checked      the download matches GitHub's SHA-256" + $(if (-not $digest) { ' (NOT CHECKED: no digest)' })) 'DarkGray'

if ($DryRun) {
    Write-Host ''
    Write-Host '  -DryRun: lists\mods.json is unchanged.' -ForegroundColor Yellow
    Write-Host ''
    exit 0
}

$text = $text.Substring(0, $start) + $span + $text.Substring($end)
[IO.File]::WriteAllText($File, $text, (New-Object Text.UTF8Encoding($false)))

Write-Host ''
Write-Host '  Written to lists\mods.json. Next:' -ForegroundColor Green
Write-Item 'git diff lists\mods.json'
Write-Item "git commit -am `"mods: $($entry.name) $Tag`" and git push"
Write-Item '.\Build-Manifest.ps1, then deploy'
Write-Host ''
