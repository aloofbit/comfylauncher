# ComfyLauncher

**Bugs, questions and screenshots: [join our Discord](https://discord.gg/uhefX2efB7).**

[![Discord](https://img.shields.io/badge/Discord-ComfyCraft-5865F2?logo=discord&logoColor=white&style=for-the-badge)](https://discord.gg/uhefX2efB7)

Little launcher project for smol server. Thanks for seeing.

![The launcher screen, with tabs for the realm, mods, addons and settings](media/comfylauncher.gif)

## Install

1. [Download](https://github.com/aloofbit/comfylauncher/releases/latest) `ComfyLauncher.zip`
2. Extract into your WoW folder
3. Open `ComfyLauncher.exe`

### Mac

ComfyLauncher runs inside [WoWSilicon](https://github.com/WoWSilicon/WoWSilicon) on Apple Silicon Macs.

1. Install WoWSilicon, choose your WoW folder and apply its game patch
2. Extract `ComfyLauncher.zip` into your WoW folder
3. Install Wine Mono one time. Download `wine-mono-11.2.0-x86.msi` from [wine-mono](https://github.com/madewokherd/wine-mono/releases), open WoWSilicon's Wine terminal and run:
   ```
   wine msiexec /i ~/Downloads/wine-mono-11.2.0-x86.msi
   ```
4. In WoWSilicon, set the launcher to `ComfyLauncher.exe` in your WoW folder
5. Start the launcher from WoWSilicon

Start the game starts the game the same way as WoWSilicon's Play button. Mods that WoWSilicon moved into `mods` show as installed.

## Features

- Download/Install Mods
- Install Addons (git or zip)
- Addons repo search
- Realm choice
- Small File
- Works through Steam overlay
- Works on Linux through Proton or Wine
- Works on Mac through WoWSilicon
- Secret features :)

## Build

Install the [.NET SDK](https://dotnet.microsoft.com/download), then run:

```
dotnet build src\ComfyLauncher.csproj -c Release
```

The exe is in `src\bin\Release\net472\`.

## Inspiration

- [IchaLaunch](https://github.com/brutaliccus/IchaLaunch)
- [OctoWoW Installation Manager](https://github.com/zmarotrix/OctoWoW-Installation-Manager)
- TurtleWoW Launcher
- Octo Launcher
