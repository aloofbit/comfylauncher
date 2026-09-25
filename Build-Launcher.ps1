# Builds the C# launcher in src\ into ComfyLauncher.exe here, the file
# Build-Manifest.ps1 zips and Install-Launcher.ps1 copies into a client.
#
# Needs the .NET SDK on this machine (dotnet), which is the owner's cost only:
# the exe targets .NET Framework 4.7.2, which Windows already has, so a player
# installs nothing. Replaced Build-Starter.ps1 on 2026-09-24, which compiled
# the old exe that carried ComfyLauncher.ps1 inside it.
#
# Built into a temporary folder, not src\bin: a copy of the launcher running
# from src\bin holds that exe open, and the build would fail to replace it.

$ErrorActionPreference = 'Stop'
$Here   = Split-Path -Parent $MyInvocation.MyCommand.Definition
$csproj = Join-Path $Here 'src\ComfyLauncher.csproj'
$out    = Join-Path $Here 'ComfyLauncher.exe'

$built = Join-Path ([IO.Path]::GetTempPath()) ('comfylauncher-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    $log = & dotnet build $csproj -c Release -nologo -v q ('-p:OutDir=' + $built + '\') 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | Where-Object { "$_" -match 'error' } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "The launcher did not build (dotnet exit $LASTEXITCODE)."
    }
    Copy-Item -LiteralPath (Join-Path $built 'ComfyLauncher.exe') -Destination $out -Force
} finally {
    Remove-Item -LiteralPath $built -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "    built $out" -ForegroundColor DarkGray
