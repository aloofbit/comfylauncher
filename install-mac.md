# Install on a Mac

ComfyLauncher runs on Apple Silicon Macs inside [WoWSilicon](https://github.com/WoWSilicon/WoWSilicon), which runs the game through Wine.

## Needs

- A Mac with Apple Silicon (M1 or later) and macOS 14 or later
- [WoWSilicon](https://wowsilicon.github.io/)
- Your Turtle WoW folder

## Install

1. Install WoWSilicon and move it to Applications
2. Open WoWSilicon, choose your WoW folder and apply its game patch
3. [Download](https://github.com/aloofbit/comfylauncher/releases/latest) `ComfyLauncher.zip` and extract it into your WoW folder
4. Install Wine Mono one time. ComfyLauncher needs it, and WoWSilicon does not include it. Download `wine-mono-11.2.0-x86.msi` from [wine-mono](https://github.com/madewokherd/wine-mono/releases), open WoWSilicon's Wine terminal and run:
   ```
   wine msiexec /i ~/Downloads/wine-mono-11.2.0-x86.msi
   ```
5. In WoWSilicon, set the launcher to `ComfyLauncher.exe` in your WoW folder
6. Start the launcher from WoWSilicon
7. Press Make a Mac app, under Start the game

From then on, open ComfyLauncher from Applications or Launchpad.

Wine Mono 11.2.0 is the version for WoWSilicon 3.2.2. A later WoWSilicon can need a later version.

## After a WoWSilicon update

Start the launcher from WoWSilicon once and press Update the Mac app. The Mac app keeps the paths and settings that WoWSilicon had when you made it.

## On a Mac

- Start the game starts the game the same way as WoWSilicon's Play button.
- Mods that WoWSilicon moved into the `mods` folder show as installed.
- There is no VanillaFixes row and no DXVK row. WoWSilicon loads the mods and supplies the graphics.

## Problems

| Problem | Fix |
|---|---|
| Nothing opens when you start the launcher from WoWSilicon | Wine Mono is missing. Do step 4. |
| The Mac app says the Wine from WoWSilicon is not where it was | Do the steps in After a WoWSilicon update. |
