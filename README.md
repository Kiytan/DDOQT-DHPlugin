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

<img width="503" height="347" alt="image" src="https://github.com/user-attachments/assets/bc75c2cd-7129-4690-80dd-fdf932791b0e" />


I've mainly done this as I find using qt much easier to sort through quests I'm missing for certain favor, or level appropriate quests than using the adventure compendium in game.

<img width="628" height="493" alt="DDOQTSite" src="https://github.com/user-attachments/assets/6b6d0fea-84a4-47e9-80e1-428e97ac261d" />


If something isn't being recorded properly, turn on **Save a log file** in the plugin window and play until it happens again. The plugin then writes `QuestTracker.log` to its folder (`%LocalAppData%\DungeonHelperData\plugins\DDOQT`), which is worth attaching to a bug report. It never takes up more than 1 MB, as it starts over (keeping the previous half as `QuestTracker.log.1`) once it reaches 512 KB.

BE AWARE: the plugin tracks quests completed on a per character basis, showing the completed quests for the currently logged in character, but the site does not, so if you swap to a different character and want to track that character on the site instead, be sure to use the sync (replace) option the first time you update the site.
(Saving for multiple characters is something I want to add to the site eventually).


## Known Issues
Quests you complete while the plugin is running are recorded at the difficulty you actually ran them on, Reaper included, as Dungeon Helper 5 reads it from the end-of-quest Experience Report. The list the game sends when you log in doesn't distinguish reaper from elite though (similar to the adventure compendium in game), so quests completed before you installed the plugin can show as elite.

I have not tested this with every quest, and some chains behave oddly (notably ones that chain directly into each other). If it's not registering as you complete the quest, logging out/in should resolve it and update the list.
