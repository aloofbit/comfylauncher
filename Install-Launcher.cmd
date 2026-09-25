@echo off
rem ===========================================================================
rem  Install-Launcher.cmd -- put a copy of ComfyLauncher inside a client, so it
rem  can be double-clicked from there.
rem
rem  Double-click this and it asks which folder. Or DRAG A WOW FOLDER ONTO THIS
rem  ICON, which is the shortest route.
rem
rem  It builds ComfyLauncher.exe and copies it into <client>\ComfyLauncher\,
rem  and nothing else. The launcher finds its client by walking up from
rem  wherever it sits, so that is the whole configuration.
rem
rem  THIS IS AN OWNER TOOL, not the thing players get. They get
rem  dist\ComfyLauncher.zip, which Build-Manifest.ps1 writes, and extract it
rem  into their own WoW folder.
rem
rem  It is a wrapper, for the same two reasons as every other .cmd here:
rem  double-clicking a .ps1 opens it in NOTEPAD, and LocalMachine execution
rem  policy is Restricted. -ExecutionPolicy Bypass clears both for this one
rem  process and changes nothing on the machine.
rem
rem  To point a copy at a test build rather than the live manifest, run the
rem  .ps1 from a prompt instead:
rem
rem      .\Install-Launcher.ps1 "C:\Games\TurtleWoW" -Source dist
rem ===========================================================================

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Launcher.ps1" %*

echo.
pause
