# Quest Tracker plugin for Dungeon Helper

A [Dungeon Helper](https://dungeonhelper.com) plugin that tracks your completed quests and syncs them to the [DDO Quest Tracker](https://qt.ddotools.xyz).

## Installation
Copy the Release Files into a Quest tracker plugin folder in the main dungeonhelper folder, by default C:\Users\UserNameHere\AppData\Roaming\Dungeon Helper\plugins.


## Use
Once dungeon helper is running with the plugin (you should see the little qt icon in the dungeonhelper tray/menu). Log in with a character, and give it a few seconds to load. 

Once loaded, the quest tracker window should show you the buttons to either
create a url you can paste into qt.ddotools.xyz or a merge/replace option. This will open a browser window and automatically update the tracker with all the quests you have completed. As you complete quests with the plugin running, it will add them to the list of completed quests,
so you can just hit sync again to update qt.

<img width="724" height="458" alt="DDOQTig" src="https://github.com/user-attachments/assets/ffb04937-9cd4-4a25-b372-43a9889f9586" />

I've mainly done this as I find using qt much easier to sort through quests I'm missing for certain favor, or level appropriate quests than using the adventure compendium in game.

<img width="628" height="493" alt="DDOQTSite" src="https://github.com/user-attachments/assets/6b6d0fea-84a4-47e9-80e1-428e97ac261d" />


BE AWARE: the plugin tracks quests completed on a per character basis, showing the completed quests for the currently logged in character, but the site does not, so if you swap to a different character and want to track that character on the site instead, be sure to use the sync (replace) option the first time you update the site.
(Saving for multiple characters is something I want to add to the site eventually).


## Known Issues
It currently doesn't differentiate between completing a quest on reaper and on elite, as the method used to get the information doesn't distinguish between the two (similar to the adventure compendium in game)

I have not tested this with every quest, and some chains behave oddly (notably ones that chain directly into each other). If it's not registering as you complete the quest, logging out/in should resolve it and update the list.
