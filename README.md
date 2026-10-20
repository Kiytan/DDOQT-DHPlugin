# Quest Tracker plugin for Dungeon Helper

A [Dungeon Helper](https://dungeonhelper.com) plugin that tracks your completed quests and syncs them to the [DDO Quest Tracker](https://qt.ddotools.xyz).

## Installation
Version 2 onwards is for **Dungeon Helper 5**. Download `DDOQT_<version>.zip` from the [latest release](https://github.com/Kiytan/DDOQT-DHPlugin/releases/latest), then in Dungeon Helper go to **Settings → "Add plugin from zip file"** and pick it. Once installed, Dungeon Helper keeps it up to date.

If you're still on Dungeon Helper 4, use the [1.1.6 release](https://github.com/Kiytan/DDOQT-DHPlugin/releases/tag/1.1.6) instead.

Moving from Dungeon Helper 4 to 5 doesn't carry over the plugin's saved quest list, but it rebuilds itself from the game the first time you log in to each character.


## Use
Once dungeon helper is running with the plugin (you should see the little qt icon in the dungeonhelper tray/menu). Log in with a character, and give it a few seconds to load. 

Once loaded, the quest tracker window should show you the buttons to either
create a url you can paste into qt.ddotools.xyz or a merge/replace option. This will open a browser window and automatically update the tracker with all the quests you have completed. As you complete quests with the plugin running, it will add them to the list of completed quests,
so you can just hit sync again to update qt.

There is also an **Export CSV** button, which saves your completed quests as a spreadsheet you can open in Excel or similar. Each row has the quest name, DDOQT quest ID, level, difficulty, favour and whether it matched a quest on qt.ddotools.xyz. Quests that didn't match are included too, so the CSV is a handy way to spot anything the site isn't picking up. A favour of 0 can mean the favour isn't known (for example, some chain quest dungeons) rather than none.

<img width="724" height="458" alt="DDOQTig" src="https://github.com/user-attachments/assets/ffb04937-9cd4-4a25-b372-43a9889f9586" />

I've mainly done this as I find using qt much easier to sort through quests I'm missing for certain favor, or level appropriate quests than using the adventure compendium in game.

<img width="628" height="493" alt="DDOQTSite" src="https://github.com/user-attachments/assets/6b6d0fea-84a4-47e9-80e1-428e97ac261d" />


If something isn't being recorded properly, turn on **Save a log file** in the plugin window and play until it happens again. The plugin then writes `QuestTracker.log` to its folder (`%LocalAppData%\DungeonHelperData\plugins\DDOQT`), which is worth attaching to a bug report. It never takes up more than 1 MB, as it starts over (keeping the previous half as `QuestTracker.log.1`) once it reaches 512 KB.

BE AWARE: the plugin tracks quests completed on a per character basis, showing the completed quests for the currently logged in character, but the site does not, so if you swap to a different character and want to track that character on the site instead, be sure to use the sync (replace) option the first time you update the site.
(Saving for multiple characters is something I want to add to the site eventually).


## Known Issues
Quests you complete while the plugin is running are recorded at the difficulty you actually ran them on, Reaper included, as Dungeon Helper 5 reads it from the end-of-quest Experience Report. The list the game sends when you log in doesn't distinguish reaper from elite though (similar to the adventure compendium in game), so quests completed before you installed the plugin can show as elite.

I have not tested this with every quest, and some chains behave oddly (notably ones that chain directly into each other). If it's not registering as you complete the quest, logging out/in should resolve it and update the list.


## Building
You need:

- the .NET 10 SDK (Microsoft's [dotnet-install.sh](https://learn.microsoft.com/en-gb/dotnet/core/tools/dotnet-install-script) works on Linux; there are no Windows Desktop targets to worry about any more, as the UI is ImGui rather than Windows Forms)
- `openssl`
- `osslsigncode` to Authenticode-sign the dll (`sudo dnf install osslsigncode` on Fedora)
- `jq`, only for publishing the update feed

```
DH_AUTHOR_PFX=/path/to/author.pfx dotnet publish -c Release
```

That produces `dist/DDOQT_<version>.zip`. Dungeon Helper 5 wants the files at the root of the zip, not under a `plugins/QuestTracker/` folder as Dungeon Helper 4 did:

| File | What it is |
| --- | --- |
| `DDOQT.dll` | The plugin, signed with the author key |
| `DDOQT.deps.json` | Its dependency manifest |
| `DDOQT.pem` | The author public key, derived from the `.pfx` |
| `DDOQT.entitlement` and `.sig` | Issued by the Dungeon Helper team, copied from `entitlement/` |
| `plugin.metadata` | The version, and the update feed this install polls |
| `DDOQT.manifest` and `.sig` | SHA-256 of every file above, signed with the author key |

Without `DH_AUTHOR_PFX` the build still produces a zip, which is handy as a compile check, but Dungeon Helper refuses to install it. It does the same without the entitlement, and the build warns about both.

VoK.Sdk (which includes ImGui.NET), log4net and Newtonsoft.Json are deliberately left out, as Dungeon Helper provides them. A genuinely third-party dependency would need adding to the `PackageForDungeonHelper` target in [QuestTracker.csproj](QuestTracker.csproj).

To test a build, set `DH_DATA_DIR` to Dungeon Helper's data folder and the build also copies the bundle into its `plugins/DDOQT` folder. That's `%LocalAppData%\DungeonHelperData` on Windows; under Steam's Proton it's `steamapps/compatdata/206480/pfx/drive_c/users/steamuser/AppData/Local/DungeonHelperData`.

### The author key and entitlement
Dungeon Helper 5 only loads a plugin with an **entitlement**: a small document, signed by the Dungeon Helper team, that ties the plugin's id (the `[PluginId]` in [Plugin.cs](Plugin.cs)) to the key its author signs with.

There are two kinds. A personal `machine.entitlement`, kept in the Dungeon Helper data folder, lets one machine load anything signed with your key, which is all you need for testing. A plugin's own `DDOQT.entitlement` travels in the zip and is what lets everyone else install it, so a release needs one. Never put `machine.entitlement` in the zip or the repository, as it belongs to one machine.

Make a `.pfx` with an empty password from your private key:

```
openssl req -x509 -new -key author.key -days 3650 -subj "/CN=DDOQT, by Kiytan" -out author.crt
openssl pkcs12 -export -out author.pfx -inkey author.key -in author.crt -passout pass:
```

Then ask the Dungeon Helper team on [Discord](https://discord.gg/bfMZnbz) for an entitlement, giving them the plugin name `DDOQT`, the plugin id `f006af62-5b8a-4298-9017-e4e588df4d7d` and your key fingerprint:

```
openssl pkey -in author.key -pubout -outform DER | openssl dgst -sha256
```

Commit the two files they send as `entitlement/DDOQT.entitlement` and `entitlement/DDOQT.entitlement.sig`. They're public, so committing them leaks nothing, but never edit them: the signature covers the exact bytes. Keep the key, `.crt` and `.pfx` out of the repository (`.gitignore` already excludes them). Changing the key means asking for a new entitlement.

### Releasing
Every install polls `https://kiytan.github.io/DDOQT-DHPlugin/V2/plugin.metadata` for updates, from GitHub Pages on the `gh-pages` branch. That URL is baked into each install, so it must never move.

1. Bump `<Version>` in [QuestTracker.csproj](QuestTracker.csproj) and build the zip as above.
2. Create a GitHub release tagged `<version>` (no `v`, matching the earlier releases) and attach the zip to it.
3. Run `DH_AUTHOR_PFX=/path/to/author.pfx scripts/update-feed.sh <version>`. It fetches the live feed, adds the new version with the release URL and checksum, and signs it into `dist/feed/V2/`.
4. Copy `plugin.metadata` and `plugin.metadata.sig` into `V2/` on the `gh-pages` branch and push. Always publish the two together, as Dungeon Helper ignores a feed whose signature doesn't match.
