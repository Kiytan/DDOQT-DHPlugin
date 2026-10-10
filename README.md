# Quest Tracker plugin for Dungeon Helper

A [Dungeon Helper](https://dungeonhelper.com) plugin that tracks your completed quests and syncs them to the [DDO Quest Tracker](https://qt.ddotools.xyz).

## Installation
Download `QuestTracker_<version>.zip` from the [latest release](https://github.com/Kiytan/DDOQT-DHPlugin/releases/latest), then in Dungeon Helper go to **Settings → "Add plugin from zip file"** and pick it.



## Use
Once dungeon helper is running with the plugin (you should see the little qt icon in the dungeonhelper tray/menu). Log in with a character, and give it a few seconds to load. 

Once loaded, the quest tracker window should show you the buttons to either
create a url you can paste into qt.ddotools.xyz or a merge/replace option. This will open a browser window and automatically update the tracker with all the quests you have completed. As you complete quests with the plugin running, it will add them to the list of completed quests,
so you can just hit sync again to update qt.

There is also an **Export CSV** button, which saves your completed quests as a spreadsheet you can open in Excel or similar. Each row has the quest name, DDOQT quest ID, level, difficulty, favour and whether it matched a quest on qt.ddotools.xyz. Quests that didn't match are included too, so the CSV is a handy way to spot anything the site isn't picking up. A favour of 0 can mean the favour isn't known (for example, some chain quest dungeons) rather than none.

<img width="724" height="458" alt="DDOQTig" src="https://github.com/user-attachments/assets/ffb04937-9cd4-4a25-b372-43a9889f9586" />

I've mainly done this as I find using qt much easier to sort through quests I'm missing for certain favor, or level appropriate quests than using the adventure compendium in game.

<img width="628" height="493" alt="DDOQTSite" src="https://github.com/user-attachments/assets/6b6d0fea-84a4-47e9-80e1-428e97ac261d" />


BE AWARE: the plugin tracks quests completed on a per character basis, showing the completed quests for the currently logged in character, but the site does not, so if you swap to a different character and want to track that character on the site instead, be sure to use the sync (replace) option the first time you update the site.
(Saving for multiple characters is something I want to add to the site eventually).


## Known Issues
It currently doesn't differentiate between completing a quest on reaper and on elite, as the method used to get the information doesn't distinguish between the two (similar to the adventure compendium in game)

I have not tested this with every quest, and some chains behave oddly (notably ones that chain directly into each other). If it's not registering as you complete the quest, logging out/in should resolve it and update the list.


## Building
```
dotnet publish -c Release
```

That produces `dist/QuestTracker_<version>.zip`, laid out as Dungeon Helper expects for the "Add plugin from zip file" dialog:

```
plugins/QuestTracker/QuestTracker.dll
plugins/QuestTracker/QuestTracker.deps.json
```

You need the .NET 8 SDK. It also builds on Linux with Microsoft's SDK (from [dotnet-install.sh](https://learn.microsoft.com/en-gb/dotnet/core/tools/dotnet-install-script)) and an extra flag, as it is a Windows Forms project:

```
dotnet publish -c Release -p:EnableWindowsTargeting=true
```

Distribution-packaged SDKs (e.g. Fedora's `dotnet-sdk-8.0`) leave out the Windows Desktop targets, so they fail with `MSB4019 ... Microsoft.NET.Sdk.WindowsDesktop.targets was not found`.

log4net and System.Drawing.Common are deliberately left out — Dungeon Helper's host provides them. Any genuinely third-party dependency would need adding to the `PackageForDungeonHelper` target in [QuestTracker.csproj](QuestTracker.csproj), the way the official Aligner plugin ships `INIFileParser.dll`.
