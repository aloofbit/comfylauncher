# Tags the launcher's version and makes a GitHub release of it, with
# ComfyLauncher.zip attached. Build-Manifest.ps1 runs this at its end, after
# the exe and the zip are built. It can also be run by hand. A tag or a release
# that exists is left alone, so a second run does nothing, and a run after a
# failed one finishes the job.
#
# THIS FOLDER IS THE REPO, github.com/aloofbit/comfylauncher, cloned into
# comfy-wow\launcher. Until 2026-09-25 the source lived in comfy-wow\launcher\app
# and Publish-Source.ps1 copied it here at each release. Now src\ and lists\
# are edited here, and pull requests land here.
#
# WHAT IT COMMITS BY ITSELF is Build-Manifest.ps1's raise of <Version> in
# src\ComfyLauncher.csproj, as "ComfyLauncher v1.14". Any other change in src\
# that is not committed stops it: the tag would name source that is not the
# source of the exe.
#
# THE HASH IN THE TAG IS MEASURED, NOT ASSUMED. HEAD's src\ is built again in
# a temporary folder, and the tag is made only when that exe is byte for byte
# ComfyLauncher.exe here. It works because the build is deterministic:
# measured 2026-09-24, a copy of the source built in another folder gave the
# shipped exe's hash. THE ZIP IS HELD TO THE SAME HASH: the release is made
# only when the exe inside it is the one the tag names, so a zip left over
# from another build cannot go out under this number.
#
# NEVER FATAL. A failure is a yellow line: the manifest is already written by
# then. Run this again to finish what failed.

param(
    # Commit and tag, but do not push and make no release.
    [switch] $NoPush,

    # The zip to attach to the release. Build-Manifest.ps1 passes its own.
    [string] $Zip = ''
)

$ErrorActionPreference = 'Stop'

$Here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Src    = Join-Path $Here 'src'
$Csproj = Join-Path $Src 'ComfyLauncher.csproj'
$Slug   = 'aloofbit/comfylauncher'
$Exe    = Join-Path $Here 'ComfyLauncher.exe'
if (-not $Zip) { $Zip = Join-Path $Here 'dist\ComfyLauncher.zip' }

function Note([string] $Text, [string] $Colour = 'DarkGray') { Write-Host "    $Text" -ForegroundColor $Colour }

# GIT AND GH WRITE PROGRESS TO STDERR, and Windows PowerShell 5.1 turns a
# native command's redirected stderr into errors. So these run under Continue
# and are judged by the exit code. NOT NAMED Git: PowerShell names ignore case
# and a function wins over an application, so `& git` inside a function called
# Git calls itself, forever. That cost five minutes on 2026-09-24.
function InRepo {
    $ErrorActionPreference = 'Continue'
    $out = & git -C $Here @args 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Out = ($out -join "`n") }
}

function OnGitHub {
    $ErrorActionPreference = 'Continue'
    $out = & gh @args --repo $Slug 2>&1 | ForEach-Object { "$_" }
    [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Out = ($out -join "`n") }
}

function Publish {
    $m = [regex]::Match([IO.File]::ReadAllText($Csproj), '<Version>([^<]+)</Version>')
    if (-not $m.Success) { throw "no <Version> in $Csproj" }
    $tag = 'v' + $m.Groups[1].Value

    if ((InRepo rev-parse -q --verify "refs/tags/$tag").Ok) {
        if ($NoPush) { Note "release: $tag is tagged"; return }
        # A push that failed last time goes now. Only the tag: main is pushed
        # by whoever committed to it.
        $r = InRepo push -q origin "refs/tags/$tag"
        if (-not $r.Ok) { throw "could not push the tag ${tag}: $($r.Out)" }
        Release $tag
        return
    }

    # Only the csproj may be uncommitted, and only its <Version> line, which is
    # what Build-Manifest.ps1 writes.
    $dirty = @((InRepo status --porcelain -- src).Out -split "`n" | Where-Object { $_ })
    $others = @($dirty | Where-Object { $_ -notmatch 'src/ComfyLauncher\.csproj$' })
    if ($others.Count -gt 0) {
        throw "$tag is not tagged: src\ has changes that are not committed. Commit them, then run Publish-Release.ps1."
    }
    if ($dirty.Count -gt 0) {
        $diff = (InRepo diff --unified=0 -- src/ComfyLauncher.csproj).Out -split "`n" |
            Where-Object { $_ -match '^[+-][^+-]' }
        if (@($diff | Where-Object { $_ -notmatch '<Version>' }).Count -gt 0) {
            throw "$tag is not tagged: src\ComfyLauncher.csproj has changes other than <Version>. Commit them, then run Publish-Release.ps1."
        }
        $r = InRepo commit -q -m "ComfyLauncher $tag" -- src/ComfyLauncher.csproj
        if (-not $r.Ok) { throw "commit failed: $($r.Out)" }
    }

    # HEAD's source, built on its own, must be the exe that was shipped.
    $work = Join-Path ([IO.Path]::GetTempPath()) ('comfylauncher-release-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        $null = New-Item -ItemType Directory -Force -Path $work
        $tar = Join-Path $work 'src.tar'
        $r = InRepo archive --format=tar -o $tar HEAD src
        if (-not $r.Ok) { throw "could not export HEAD's src: $($r.Out)" }
        $ErrorActionPreference = 'Continue'
        # Windows' own tar by its full path: Git's GNU tar can come first on
        # PATH, and it reads the C: of a path as a host to connect to.
        & (Join-Path $env:WINDIR 'System32\tar.exe') -xf $tar -C $work 2>&1 | Out-Null
        $sdk = "$(& dotnet --version)".Trim()
        $null = & dotnet build (Join-Path $work 'src\ComfyLauncher.csproj') -c Release -nologo -v q ('-p:OutDir=' + (Join-Path $work 'out') + '\') 2>&1
        $code = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($code -ne 0) { throw "HEAD's src did not build (dotnet exit $code)" }
        $built = Join-Path $work 'out\ComfyLauncher.exe'
        if (-not (Test-Path -LiteralPath $Exe) -or
            (Get-FileHash -LiteralPath $built).Hash -ne (Get-FileHash -LiteralPath $Exe).Hash) {
            throw "an exe built from HEAD is not ComfyLauncher.exe here, so $tag is not tagged. Run Build-Launcher.ps1, then this again."
        }
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
    $hash = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()

    $r = InRepo tag -a $tag -m "ComfyLauncher $tag" -m "ComfyLauncher.exe SHA-256: $hash`nBuilt with .NET SDK $sdk"
    if (-not $r.Ok) { throw "tag failed: $($r.Out)" }
    Note "release: tagged $tag, exe $($hash.Substring(0, 12))" 'Green'

    if ($NoPush) { Note 'release: not pushed (-NoPush)'; return }
    $r = InRepo push -q origin HEAD:refs/heads/main "refs/tags/$tag"
    if (-not $r.Ok) { throw "could not push ${tag}. If GitHub has commits this folder does not, git pull, then run this again: $($r.Out)" }
    Note "release: pushed $tag"

    Release $tag
}

# THE RELEASE: the zip players download, on the tag that says what is in it.
# The notes repeat the tag's hash and SDK, since a release page is where
# somebody holding the zip will look.
function Release([string] $Tag) {
    if ((OnGitHub release view $Tag).Ok) { Note "release: $Tag is released"; return }

    $message = (InRepo tag -l --format='%(contents)' $Tag).Out
    $m = [regex]::Match($message, 'SHA-256: ([0-9a-f]{64})')
    $s = [regex]::Match($message, 'SDK (\S+)')
    if (-not $m.Success) { throw "the message of tag $Tag has no SHA-256, so no release" }
    $want = $m.Groups[1].Value

    if (-not (Test-Path -LiteralPath $Zip)) { throw "no $Zip to attach, so no release. Run Build-Manifest.ps1." }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $entry = @($archive.Entries | Where-Object { $_.Name -eq 'ComfyLauncher.exe' }) | Select-Object -First 1
        if (-not $entry) { throw "no ComfyLauncher.exe in $Zip, so no release" }
        $stream = $entry.Open()
        try { $got = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash.ToLowerInvariant() }
        finally { $stream.Dispose() }
    } finally { $archive.Dispose() }
    if ($got -ne $want) { throw "the exe in $Zip is not the one tagged $Tag, so no release. Run Build-Manifest.ps1." }

    $notes = @(
        'Extract the zip into your WoW folder and start ComfyLauncher.exe.'
        ''
        "ComfyLauncher.exe SHA-256: $want"
        $(if ($s.Success) { "Built with .NET SDK $($s.Groups[1].Value)" })
    ) -join "`n"
    $r = OnGitHub release create $Tag $Zip --verify-tag --title "ComfyLauncher $Tag" --notes $notes
    if (-not $r.Ok) { throw "could not make the release ${Tag}: $($r.Out)" }
    Note "release: released $Tag with $(Split-Path -Leaf $Zip)" 'Green'
}

try { Publish } catch { Note ("release: " + $_.Exception.Message) 'Yellow' }
