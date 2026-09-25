# ComfyLauncher

**Bugs, questions and screenshots: [join our Discord](https://discord.gg/YSWzYk8xP).**

ComfyLauncher is the launcher for ComfyCraft, a World of Warcraft private server. It works with a Turtle WoW 1.18.1 client. It installs ComfyCraft's addons, client patches and client mods into the client. It keeps them up to date, sets the realm and starts the game.

This repository holds the source code of `ComfyLauncher.exe`. Read the code to see what the exe does. Build the code to prove that your copy of the exe is this code.

Get the launcher from [comfycraft.dedyn.io/play](https://comfycraft.dedyn.io/play).

## What the launcher changes

The launcher writes to two folders only: the WoW folder, and the folder that holds `ComfyLauncher.exe`. It does not write to the registry.

In the WoW folder, the launcher can:

- Add, update and remove addons in `Interface\AddOns`.
- Add, update and remove patches in `Data`. To turn a patch off, it adds `.off` to the file name.
- Add and remove client mods (DLL files) and their lines in `dlls.txt`.
- Install the DXVK version that you choose.
- Write `realmlist.wtf`, and the `SET` lines for the realm and the display settings in `WTF\Config.wtf`.
- Delete the cache in `WDB`, if the setting "Clear the cache" is on.
- Start `VanillaFixes.exe`, or `WoW.exe` if VanillaFixes is not in the folder.

In its own folder, the launcher keeps:

- `comfylauncher.json`: your choices.
- `set-aside`: files that the launcher took out of the WoW folder, so that they can be put back.
- The files that it downloads, until they are installed.

To update itself, the launcher replaces `ComfyLauncher.exe`.

The launcher reads `https://comfycraft.dedyn.io/launcher/manifest.json` and downloads the files that the manifest names. Some of these files come from GitHub. All of its network requests are downloads.

## How to build the launcher

You must have the [.NET SDK](https://dotnet.microsoft.com/download). The exe runs on .NET Framework 4.7.2, which is part of Windows 10 and Windows 11. Players do not install anything.

```
dotnet build ComfyLauncher.csproj -c Release
```

The exe is `bin\Release\net472\ComfyLauncher.exe`.

## How to check your copy of the exe

The build is deterministic. The same source code and the same SDK version make the same bytes. Each release has a tag, for example `v1.13`. The tag message gives the SHA-256 of the released exe and the SDK version that built it.

1. Get the source code of your release: `git checkout v1.13`.
2. Build the launcher.
3. In PowerShell, get the hash of each exe:

   ```
   Get-FileHash bin\Release\net472\ComfyLauncher.exe
   Get-FileHash <your folder>\ComfyLauncher.exe
   ```

4. Compare the two hashes. If they are the same, your exe is this code.

A different SDK version can make different bytes. Use the version in the tag message.

The launcher shows its release number below its banner.

## Licence

GPL-3.0. See [LICENSE](LICENSE).
