# What's new

Changes in each version, newest first. Versions before 2.7.0 are only in Chinese; see CHANGELOG.md.

## 3.4.0 (2026-11-02)

- **Memos** (new, F10 to show or hide): a small window floating over the game that keeps track of "get this done by that date". Search for a condition among stats, affection, talents, facilities, items, flags, and the layers and progress of martial arts, forging and alchemy, then set a target and a deadline; the window shows the current value, progress and periods left at all times, and notes at its top when a goal is met, stops being met, is about to be due or is overdue. It can be dragged, resized and collapsed, and its background follows **Tooltip opacity** in the settings (PR #40).
- **Speed up battles too** (new setting, off by default): game speed also applies to battles. The UI now calls team battles "battles", as the game does.
- **Older game versions**: the mod also runs on game versions from before the story log rework (1.0.6000.x); option hints and the Coffee Companion window no longer throw errors there. Save anywhere is turned on there only after the save and load code checks out; otherwise it says it needs 1.0.6001 or later.
- **Pick random events**: besides the changes each event always makes, it now also lists the changes that depend on conditions, one branch per line with the condition and its current value; random events on the Now tab show the conditions too.
- **Time-limited story reminder**: stories whose time limit is written into their conditions, rather than set as a trigger window, are reminded too.
- **Story map**: quest trigger conditions are complete, and conditions are joined with "and" (they used to read "or"); a quest that triggers in several places is shown as one; connecting lines are laid out more clearly (PR #39).
- **Route map**: fixed the game freezing for a few seconds on the first search.
- **Numbers**: fixed numbers on the duel status screen and elsewhere wrapping to a second line and being cut off at lower resolutions.
- **Conflict warning**: when another mod changes the same game code, the warning now says some features may not work and suggests removing it if something goes wrong, instead of telling you to remove it.

## 3.3.3 (2026-11-01)

- **Time-limited story reminder** (new setting; Assist and higher presets turn it on): when a story whose conditions are met but hasn't triggered yet has 3 periods or fewer left in its window, the top left of the training map shows how many periods are left, until when, and where to go to trigger it; the Now tab marks these quests too.
- **Route map**: clicking a stop shows "Next" at the top with the next stop's date, place and conditions; new "Options in this stop", folded by default, lists each option's conditions and results; a side quest step shows the next step and which main stop it rejoins. "Current main" is now "Back to now", and opening the route map selects the current stop. Parallel routes such as the three study-abroad routes each keep their own lane instead of crossing; lines that split off or rejoin no longer run along the main line; the next stop has a gold ring.
- **Now tab**: fixed the options being missing when option hints are off, and the options of action menus (rest, chat and so on) not being recognized as the ones on screen; results read the same as next to the in-game options.
- **AI companion**: fixed streamed answers losing what was already written when the AI looks something up partway.
- **One-click set**: also changes stats locked on the Stats tab; before, clicking did nothing.
- **Unlimited training and forging**: "Next level" in training, forging and alchemy now reaches the next level in one click instead of two.
- **Flag names**: fixed a wide gap between some flag names and their values.
- **Korean and English UI**: corrected the "Advanced" preset description (PR #38).

## 3.3.2 (2026-10-31)

- **Pick the branch before a check**: fixed the picker being covered by the black screen when it stops at the start of a scene or before a fade-in, so it could neither be seen nor used. When the same check comes up several times in one scene, it now follows your first pick instead of asking again.
- **Save anywhere**: fixed save-anywhere saves made after loading an older save failing to load later (execution record mismatch); such saves already made now load too.
- **Save-anywhere tab**: the list can be grouped by save slot, save time or game date, and keeps the order you picked.

## 3.3.1 (2026-10-31)

- **Search**: search boxes in the story review take pinyin initials; in the Korean UI you can type initial consonants, and in the English UI the first letters of the words.
- **"Advanced" preset**: a new preset between **Assist** and **All on**. Story aids are on; picking the branch before a check and forcing events and options whose conditions aren't met are off; the only cheats are unlimited training and forging and free forging money.
- **Lecture hall**: the unlock display now has three modes, Off, Hints only and Unlock all; hover over an ending or death to see how to reach it. Assist and Advanced use Hints only, All on uses Unlock all, As intended is Off.
- **Game speed**: team battles and story lines that advance by themselves stay at normal speed.
- **Pick the branch before a check**: new setting **Stop for seen branches too** (off by default); when on, it also stops when another branch has lines you've already seen.
- **Korean UI**: filled in missing translations and corrected names, condition texts and numbering; switching the UI language now refreshes random event names, branch destinations and map hints.

## 3.3.0 (2026-10-31)

- **Pick the branch before a check** (new setting, off by default; only the **All on** preset turns it on): at a story check that branches on conditions, the game stops first when another branch has unread lines or leads to a different scene, lists each branch's conditions, current values and whether its lines were seen, and takes the branch you pick. By default values are left alone and only this one check goes that way; with **Change values to fit the chosen branch** on (off by default), stats, affection and flags are first changed just enough for that branch.
- **Force events whose conditions aren't met** (new setting, off by default; only the **All on** preset turns it on): the event picker also lists events whose conditions aren't met, with what they need; clicking one triggers it regardless, without changing flags.
- **Force options whose conditions aren't met** (new setting, off by default; only the **All on** preset turns it on): options that are hidden when their conditions aren't met are listed too, marked **Conditions not met** in red, with the conditions on hover; options already picked aren't listed. Locked options whose conditions can't be set get **Force unlock**, which unlocks them without changing values.
- **Story jump**: the confirmation page lists the condition checks inside the target scene and defaults to the branch with unread lines, which is taken directly after the jump. Jumping back no longer fills in the main quests in between; fixed jumping back filling in main quests from later chapters.
- **Pick random events**: keyboard and gamepad can reach every entry; fast-forward stops when the picker pops up; Esc no longer opens the game menu and stalls the story while it's open; it's no longer hidden behind the story's fade to black.
- **Unlock**: the button now uses the same style as the hint boxes.
- **Conflict detection**: mods that change the same game code as this one but work alongside it are no longer flagged for removal; only real conflicts are reported.

## 3.2.5 (2026-10-30)

- **AI Companion**: characters Zhao Huo hasn't met yet in the current playthrough no longer act as if they know him; how they address him only follows the story read in this playthrough; story scenes that only mention a character are no longer treated as things she went through with Zhao Huo; after switching saves, the current progress is what counts.
- **Story Jump**: the romance option only becomes available once the time of her last romance scene is reached; characters without a romance scene in the game can't be selected.
- **Korean UI**: completed the UI translation, corrected translations of story, condition and quest names, and fixed untranslated text in combined or shortened strings; also added some Japanese and English entries.
- **Duel enemy action forecast**: the horizontal layout no longer shows Chinese in the Korean, Japanese and English UI.
- **Installer**: added a "No online update check (install this zip's version)" option; when checked, the installer doesn't connect to GitHub.

## 3.2.4 (2026-10-30)

- **Mod not loading when started from Steam**: after the game was started once without Steam, later starts from Steam could skip the mod. Fixed; for an existing install, run the installer once.
- **Story branch panel**: no longer disappears right away when a battle starts or you return to the map; it hides after the panel auto-hide time.
- **Installer diagnostics**: when the mod does not load, the export also checks whether the game loaded the loader, how the game was started, and system and security-software blocking settings.

## 3.2.3 (2026-10-30)

- **AI Companion wait time**: the request is only stopped after the provider sends nothing for 5 minutes, so models that think for a long time are no longer cut off before answering.
- **Gemini error in fast mode**: gemini-3.8-flash returned a 400 error and could not connect in fast mode. Fixed.
- **Installer**: the Other mods list fills the rest of the left column and grows when the window is taller; on short screens the banner gets shorter to make room for the list.

## 3.2.2 (2026-10-30)

- **AI Companion fast mode**: a new **Answers** row above the chat offers Fast, Standard and Deep. Fast looks things up fewer times, uses shorter instructions and keeps the model from thinking long, so free tiers and small local models answer sooner; answers are rougher.
- **AI Companion options above the chat**: answers, spoilers and the heroines' strategy hints are picked right above the chat; hover an option for what it does.
- **AI Companion answers in the UI language**: the instructions sent to the model are written separately for each UI language (Simplified and Traditional Chinese, English, Korean, Japanese), and an answer that still comes back in another language is rewritten once. The lookup details follow the UI language too.
- **Gemini preset**: gemini-3.6-flash for normal answers and gemini-3.8-flash for deep mode. Saved model names stay as they are; pick the provider again to get the new ones.
- **English and other UIs**: tabs at the top, the character row on the AI page and the suggested questions no longer run long or get cut off; Tang Moling and Tang Buyi now have English names.

## 3.2.1 (2026-10-29)

- **AI Companion reply language**: follows the language of your latest message, so a question in Traditional Chinese gets a reply in Traditional Chinese; when it can't tell, it uses the UI language.
- **AI Companion and affection**: in companion chat, "your affection for me" means her (his) affection for Zhao Huo; changes come as a confirm card instead of being written out as text.
- **Provider presets**: Gemini now uses gemini-3.8-flash with a cap on thinking time to avoid timeouts; OpenAI deep mode uses gpt-6-sol; SiliconFlow uses DeepSeek-V4-Flash, with DeepSeek-V4-Pro for deep mode. Saved model names stay as they are; pick the provider again to get the new ones.

## 3.2.0 (2026-10-29)

- **Story jump**: pick a scene on the route map or story map and click **Jump here** to play from there, even one you haven't reached yet. Before the jump you see the choices along the way that affect what comes later; the progress in between is filled in the way you pick, and you can bond with one of the heroines too. A save-anywhere save is made first, so you can always go back. Can be turned off under Settings > Story. Not recommended for normal play.
- **Story map**: every scene box lists its ways on, one per row, with a line from that row to the scene it leads to; options that stay in the scene are listed too. Hover a row for the full condition and what it changes.
- **Route map**: the main story is one line; branches split off above and below and come back where they rejoin, with the trigger condition on each split. Stop names no longer overlap.
- **AI Companion couldn't connect**: for some players **Test connection** and sending a message did nothing. Fixed.
- **Faster start**: parsed scripts are kept, so they aren't all read again on every start unless the game or Coffee Companion has been updated.
- **While studying away**: the place lists in Kongtong and Qingcheng also show the random events popup, grouped by the options in each place and listing only the options you can pick now; the Now tab no longer lists Tang clan side quests you can't reach.
- **Option results**: the last option in a group no longer claims the changes everyone gets after the group ends.
- **Korean UI**: random event names are in Korean; result words in names like 「P_成功_001」 follow the UI language.
- **English UI**: the save-anywhere limit and the note on the save-anywhere tab are no longer in Chinese.

## 3.1.0 (2026-10-28)

- **Random events of building options**: inside a training building, hover over an option such as chopping wood or training (or select it with the arrow keys or a gamepad) to see the random events it can roll, their odds and which you have not seen; the same goes for moving and peddling in the Go out list.
- **Now tab**: random events here that you have not seen are marked.
- **"Not seen"**: random events with an outcome that has no dialogue leave nothing in the read log when they happen, so they are no longer always marked "Not seen".
- **"All on" preset**: no longer turns on "Break stat cap".
- **Settings**: setting names that did not fit in Japanese, Korean and English are shorter, and names that still don't fit are shown in a smaller font; the "Title screen partner" row is now as tall as the others.

## 3.0.9 (2026-10-28)

- **Stat presets**: values you change on the Stats tab are recorded; click "Stat presets" to save them as a preset. After loading another save or starting a new game, click Apply to set them all at once; values you didn't change are left alone.
- **Love interest portrait on the save screen**: if you have bonded with her in that save, the detail pane shows a shy expression.
- **Numbers cut off with larger game text**: numbers that no longer fit their box (the six main stats, forging levels and so on) are now shown instead of being cut off.
- **Gain and loss colors in option results**: light green and red on dark backgrounds, dark green and red on light ones as before; a new "Color gains and losses in option results" switch under Settings > Story turns the colors off.

## 3.0.8 (2026-10-28)

- **Save-anywhere saves still load after game updates**: before loading, each story script the save passed through is checked; only saves whose scripts were changed by an update can't be loaded (the list says why), and the rest load as usual. If a load still goes out of step, you get a notice and return to the title screen instead of a black screen.
- **Save-anywhere ambient sound**: ambient sound that the story had already stopped no longer starts again after loading (for saves made with this version or later).
- **Colored choice outcomes**: gains are green and losses red, on dialogue choices, dice checks, hover tips and in the Coffee Companion window.
- **AI guide**: an empty chat offers a few starter questions to click; it sees which choices and destinations you have read, and recommends unread ones not yet taken this playthrough first; it tells who is speaking in the current dialogue.
- **Break stat caps**: now also covers sword, hidden weapons, fist, Confucianism, Taoism and Buddhism.
- **Save-anywhere limit**: only old saves without notes are deleted; saves with notes are kept and don't count toward the limit.
- **Text boxes**: typing in a Coffee Companion text box no longer opens the backlog with L on the training map.
- **Hover tips**: in Simplified and Traditional Chinese, text wraps by character width, and marks such as "）" and "，" no longer start a line.

## 3.0.7 (2026-10-27)

- **Fixed battles being won the moment they start**: the Stats tab's "allow negative values" also affected enemy factions' health, so enemies fell as soon as they appeared. It now applies only to the hero's own stats.
- **Right-click hide UI**: with the system menu or the status (training) screen open, right-click closes them as usual.
- **"Original" sort on the load screen**: by slot, with every empty slot shown; click an empty slot to start a new game there.
- **Pass hotkey**: new in the Settings hotkeys, unbound by default.
- **Save anywhere**: filter by story, training or with notes; grouped by in-game month, foldable, with group delete (noted saves are kept); no longer deleted automatically by default, set a limit in Settings.
- **Traditional Chinese UI**: characters such as 劍聖 and 症 now show correctly.
- **Export diagnostics**: if the desktop can't be written, saves to Documents or another folder instead and selects the file in Explorer.
- **Installer**: if another tool switched the mod loader off, reinstalling turns it back on.

## 3.0.6 (2026-10-27)

- **Updated for game version 1.0.6001**: check records go into the new backlog, only during story scenes and in small text; identical checks are recorded once per period, and quests only when they actually trigger.
- **Save anywhere**: after going back to a point, the backlog no longer repeats what came after it.
- **Branch saves**: recorded normally on the new game version.
- **Condition data**: story checks and duel round conditions updated to the new game version.

## 3.0.5 (2026-10-27)

- **The AI guide can look up items and development projects**: what an item does when used, which stories give it, check for it or take it, and which month's shop sells it; for forge, alchemy and poison projects, which project has to reach which level before it appears, and what it can evolve into.
- **The AI guide covers more game systems**: what Fourth Brother's shop has now and what the food does; sect size, sect funds, and each facility's income and effects per period; for martial arts manuals, the points each level needs, the training requirements and what each level changes; what talents do and where they come from; gallery rewards for a new game plus.
- **Fuller AI lookups**: stats and skills such as mood, temperament, change of heart and sect size can be looked up for where they are checked and changed; branch checks now show what happens on that branch. Deaths that no story or duel leads to are reported as unreachable in the current version.
- **AI reply layout**: text wraps by character width, "·" and "1." no longer sit alone on a line, and no half characters show at the end of a line.
- **Fewer branch saves**: they now follow the game's autosaves and are recorded only at story save points and at the start of each period, not at every key option.

## 3.0.4 (2026-10-26)

- **Easier to bring the UI back**: after hiding the UI with right-click in a story, right-click, left-click, any other key or a gamepad button restores it. F1-F12, the screenshot key, Alt, Ctrl, Shift, Win and Tab don't count.
- **The AI guide sees your progress in this playthrough**: flag lookups and quest lookups include each flag's current value, so whether you can still reach a scene is answered from your quest progress and sect.
- **AI companion input box**: the text cursor shows reliably after you click into it.
- **Windows can't be dragged off-screen**: part of the Coffee Companion title bar always stays on screen, and the random event picker stays fully on screen.

## 3.0.3 (2026-10-26)

- **Fixed time jumping twice after loading a save made mid-story**: if the story had already moved time forward, the save now records the time from the start of that scene, so replaying it after loading doesn't skip again and main quests that only trigger in one period aren't missed.
- **Steadier AI companion connections**: the last chunk of a streamed reply from OpenAI and similar providers no longer causes an error, so long chats no longer end with "Unable to write data"; replies have no total time limit and only count as stuck after 120 seconds without data; busy providers are retried twice automatically.
- **More accurate AI guide**: when there are several ways to do something, all of them are listed; random events on the study-abroad maps can be found by place name; duels spell out how the wake-up count from talk attacks builds up; with Claude, running out of lookup rounds no longer resets the cache.
- **New settings**: "Map hints: triggerable only", "Develop gear without story requirements" and "Show item uses" (item descriptions in the bag and shops end with what using the item does and which stories check for it or take it).
- **Save page**: with "Newest first", an empty slot comes first.
- **Stats tab**: "Sect contribution" is renamed "Sect contribution threshold (10-50)"; it is a threshold the sect uses, not your contribution.

## 3.0.2 (2026-10-26)

- **Fixed Claude showing "（AI 没有说话）" (no reply) after looking things up**: within one question, Claude's thinking is now passed back unchanged, so the cache no longer resets every round; if a reply still comes back empty, the AI is asked to write the answer directly from what it found.
- **Steadier lookups with DeepSeek, Kimi and others**: within one question, the model's thinking is passed back unchanged, and Gemini 3's thought signatures are kept too.
- **The guide looks things up right away** instead of first asking whether it should.

## 3.0.1 (2026-10-26)

- **Tang Buyi, the Eldest Brother, joins the AI companion**: like the heroines, he speaks in his own voice, his portrait changes expression with what he says, and he only knows the story you have read.
- **Export chat**: a new Export chat button at the top of the AI companion saves the current chat to the desktop as Markdown.
- **Steadier long chats**: when the chat history gets too long, the older part is condensed into a summary and the recent part is kept as is; answers go by what was looked up this time instead of repeating earlier replies.
- **Fixed side quests that write no story records always showing "not taken this run" on the route map**: it now checks how far that quest line has progressed in the save.

## 3.0.0 (2026-10-25)

- **AI companion** (F3, a new tab in the Coffee Companion window): connect a large language model with your own API key. Supports DeepSeek, Kimi, Qwen, Zhipu GLM, SiliconFlow, OpenRouter, OpenAI, Claude, Gemini, local Ollama and any OpenAI-compatible endpoint.
  - **Guide**: looks up the game's own story data before answering: quests and their triggers, script branches and dialogue, affection and stat totals along each route, endings and deaths, romance routes, character bios, location events, duel conditions, plus your current progress and the options on screen.
  - **Nine heroines**: they speak in their own voice, their portraits change expression with what they say, how they address you follows the story, and they only know the story you have read. When you ask for help, a separate guide tip follows the reply; you can also set them to chat only.
  - Three **spoiler** levels; **deep mode** switches to a stronger model that checks the facts before answering.
  - When the AI wants to change stats, affection, your sweetheart, story flags or this mod's settings, it shows a card first; nothing changes until you confirm.
  - Chats are stored locally; start new ones, switch between them, delete them. Replies appear as they are written, and the header shows tokens used and cache hits.
  - Your key and chats go only to the provider you choose, and the key is hidden in diagnostics exports. A debug log can be turned on in the settings and is included in diagnostics exports. On the Steam Deck, tapping the input box opens the on-screen keyboard.
- **Window text size**: new in the 「咖啡伴侣」 (Coffee Companion) group of the settings page. It enlarges the text and buttons in this mod's windows, small panels and tooltips, for small screens such as the Steam Deck.
- **Fixed the route map counting another save's progress as side quests "taken this run"**.

## 2.9.3 (2026-10-25)

- **Game text size**: new in the 「其他」 (Other) group of the settings page. It enlarges all text in the game (menus, buttons, numbers, dialogue) for small screens such as the Steam Deck; dialogue text size stacks on top of it.
- **More Korean translations**: over a hundred missing Korean interface texts are added, and quest names with arrows or ticks on the route map are translated too.

## 2.9.2 (2026-10-24)

- **Fixed loading an anytime save getting stuck**: loading a long anytime save could use up the game's portrait animation capacity and leave the story frozen on one scene. The capacity is now larger, and if the script fails while loading, the load stops at once with a message instead of waiting.
- **The random event picker marks events you haven't seen**: they show **Not seen** after their odds.
- **The branch save load confirmation no longer cuts off text**: the box grows to fit long segment names.

## 2.9.1 (2026-10-24)

- **English interface**: 「界面语言」 (Language) on the settings page now has English; with an English patch installed, 「跟随游戏」 (Follow game) shows English automatically.
- **Jump from Collection**: each ending, death, achievement and CG lists the quests that lead to it; click one to open that quest's story map.
- **Watch the credits**: the ending CG page in Collection can replay the end credits from finishing the game. Press Esc to stop; the previous music comes back. Only works in a story scene.
- **Martial manual layer bonuses**: under martial training and books on the stats page, hover over a martial manual to see what each layer adds when learned; learned layers are ticked.
- **The installer no longer downgrades**: when it cannot download the latest version, it does not install the copy in the zip if that is older than the installed one.
- The zip now has an English readme, Readme (English).txt; Chinese text in the installer's English interface uses Microsoft YaHei.
- **Fixed the legend page in the library not showing its page buttons.**
- **Fixed 「直接获胜」 in the team battle pause menu showing in Simplified Chinese in the Traditional Chinese interface.**

## 2.9.0 (2026-10-24)

- **Presets**: at the top of the settings page, switch in one click to 「原汁原味」 (as the game intended), 「辅助」 (assist) or 「全开」 (everything on). They only change story hints, life-sim and combat aids and cheats; changing any of those makes it 「自定义」 (custom), which you can switch back to. New players pick one the first time they open the window; the default is 「辅助」.
- **Collection** (formerly Endings): records from all playthroughs, with two new groups, story (how much of each quest's dialogue you have read) and ending CGs.
- **Romance routes**: pick a love interest on the route map to light up only her scenes and see each step, whether it is done, where to get it and what that scene needs right now.
- **Stats can be set below the bonus from manuals and the like, even below zero**, and stay there through later story changes and loading.
- **Unlimited training, forging and alchemy no longer use up attempts or points**, instead of topping points up when the screen opens.
- **A sort switch at the top right of the load page** (by slot / newest first); the setting for it is gone from the settings page.
- **Fixed rerolling dice with 「逆天」 showing no number.**
- Settings tidy-up: the pinball score multiplier is gone; 「决斗回合间不干等」 is now part of 「流程提速」; branch saves moved to the save group; new installs default to the dark window theme and 0.8 window opacity.

## 2.8.3 (2026-10-23)

- **More stat locks**: mood, 性情, 处世, 修养, 道德, yin-yang and silver each have a lock on the stats page, and several can be locked at once. A locked stat can't be changed by story, options or training; changing it by hand on the stats page still works. Locks stay on across restarts and saves.
- **Fixed leftover portraits after loading an anytime save or rewinding with F4.**
- **「后台继续运行」 (keep running in the background) is now off by default**, including for players who had turned it on; turn it on in the other group of the settings page if you want it.
- **Friendlier package for non-Chinese players**: the installer is now 「咖啡伴侣安装器 Installer.exe」; conflicting old mods are moved to `BepInEx\DisabledMods`; the readme comes in Simplified Chinese, Traditional Chinese, Japanese and Korean.
- The stats page's +/- buttons are right-aligned.

## 2.8.2 (2026-10-23)

- **The save and load screens open on the save you made last**, scrolled into view.
- **Save list order**: 「存档列表排序」 in the save group of the settings page lists manual saves by slot (default) or newest first.
- **Choose who appears on the title screen**: 「标题画面人物」 in the other group of the settings page follows your ending (default), shows no one, or shows a chosen character. Only the title screen changes, not your ending records.
- **Relationship lookup**: click a name under relationships on the stats page to list the scenes that check that relationship, with links to the story map.
- **Map hints no longer list quests tied to the 修罗场 (jealousy) event while they cannot trigger.**

## 2.8.1 (2026-10-22)

- **Three modes for option results**: 「选项后果」 in the story group of the settings page shows them always (default, as before), only on hover (when the mouse is over a dialogue option or dice line, or it is selected with a gamepad; no spoilers unless you look), or never.
- **「总是」 and 「不出」 for random events**: each event in the random event picker can be set to always or never; the rule is remembered and applies even with the picker off.
- **Pick branches decided by a random number**: branch checks that roll a random number (such as being pulled into a run by 小梅 while training) now open the picker too, listing the paths, where they lead and their odds; the other conditions are still checked. Each path can also be set to always or never.
- **Fixed flag notices overlapping or appearing on the right**: several notices at once are now stacked in the middle of the screen.
- **Fixed the note box on the original save screen**: the title was hidden behind the input box, and clicking Save or Cancel could load the save underneath.

## 2.8.0 (2026-10-22)

**New**

- **Legend slot pages**: the legend save screen after an ending and the legend page in the library get page buttons, 5 pages of 12 slots by default (1 page in the base game); saving opens the first page with a free slot. Set the page count under 「传奇栏位页数」 in the save group of the settings page.
- **Search the route map by flag**: the route map search box also matches flag IDs and descriptions, listing the scenes that change or check the flag, each with a link to the story chart.
- **Search in Traditional Chinese, Korean and Japanese**: the search boxes also match the text as shown in the UI language.
- **Movable, resizable enemy forecast in duels**: drag the bottom-right corner to resize; when narrow it switches to one line. Position and size are remembered.

**Interface**

- **Consistent windows and hint boxes**: the random event picker and save note editor use the same paper colors as the Coffee Companion window and follow dark mode; map hints, the branch window, the duel forecast, the fast-forward notice and anytime save loading progress share one dark hint box style.
- **Opacity settings in one place**: the Coffee Companion group of the settings page has three opacity settings, for the main window, small windows and hint boxes, applied immediately. The old branch window and map hint opacity settings are merged into the hint box one.
- **Check records in the backlog no longer show a speaker name**: only the small check line remains, so it isn't mistaken for dialogue.

**Fixes**

- **Read lines turning white again after loading**: after loading an auto save, a recent save or an anytime save, lines you had just read showed as unread; read records are no longer lost.
- **Flag notice duration had no effect**: notices vanished at once whatever the setting, and disappeared on scene changes; they now stay for the set number of seconds across scenes.
- **Traditional Chinese wording**: after character-by-character conversion, terms are now replaced with Traditional Chinese usage, and common wrong characters are fixed. The installer too.
- **Clearer installer message when the download fails**: if the new version can't be downloaded from GitHub, the installer says which version from the zip was installed and where to get the new zip, instead of just "Installed".

## 2.7.3 (2026-10-22)

- **Map event hints fixes**:
  - Side quests whose conditions are met now show as ready to trigger (they always showed as not ready before). When the main quest requires you to go elsewhere, the hint says clicking here won't trigger anything.
  - Only buildings with a main or side quest you can trigger now and haven't played yet stay lit; unseen random events no longer count, and lit buildings no longer flicker.
  - Time limits written in quest conditions are shown as dates, and quests that are past or not yet due go under "not in time".
  - Random events list only the ones that can be drawn, sorted by odds; very long hints shrink to fit. Events you have read any line of are no longer marked as unseen.
  - The hint box has a dark background; its opacity can be set under 「地图提示不透明度」 in the training group of the settings page (default 0.8).
  - Before you've entered any story scene after starting the game, the random events section says it needs one story scene first.
- **No more false conflict warning with the Japanese patch**; when another mod patches the same game code, the warning shows its plugin name instead of an ID.

## 2.7.2 (2026-10-22)

- **Fixed option results under dice lines overlapping or running off screen**: some flag names ended with an invisible line break that made the second half overwrite the first. Long results now stay on one line and shrink if they would run past the right edge of the screen.
- **The installer warns about non-English paths**: when the game is installed in a path with Chinese or other non-English characters, BepInEx fails to load and the mod doesn't work. The installer now shows a red warning under the game folder and asks again before installing; exported diagnostics flag it too.

## 2.7.1 (2026-10-21)

- **Fixed the random event picker not showing up in Tang sect buildings** such as the kitchen and the forge: the map event hints added in 2.7.0 recorded those buildings' random events as empty while on the training map.
- The random events section of the map event hints now lists events properly.

## 2.7.0 (2026-10-21)

**New features**

- **Dark mode**: under 「窗口配色」 (window colors) in the Coffee Companion group of the settings tab, choose light, dark or follow Windows. Dark mode is ink-black with paper-colored text.
- **Event hints on the training map**: hover over a building to see its main and side quests (whether they can trigger now, time window and conditions, met in green and unmet in red) and the odds of each random event. Buildings with events you haven't seen yet stay lit. Can be turned off in the training group of the settings tab.
- **Where flags, books and items are used**: on the stats tab, click a name or its description to list every story that changes it (set, add or subtract, gain, lose) and every place that checks it, with the quest, time, place and how to get there. Each entry can jump to the story graph.
- **Flag categories**: flags on the stats tab are split into sect favor, characters, the Six Paths, story, events and side quests, dialogue options, combat, training and facilities, and other.
- **Export legend**: the game's Status → Legend panel gets an Export button that saves your current save's unfinished legend as a txt file on the desktop. The base game only lets you export at an ending.
- **Ending numbers**: the ending list shows the number from the in-game codex (「结局N」 for endings, 「N.」 for deaths) and can be searched by number. It is sorted by number by default and can be switched to sort by time.
- **Enemy move forecast in duels** (off by default): while you pick a move, the right edge of the screen shows the move the enemy has picked, and marks turns where the enemy may change it in response. Turn it on in the combat group of the settings tab.

**Hotkeys**

- Every hotkey can be unbound. The separate switches for hiding the UI, fast-forward and the stats hotkey were folded into this.
- Hotkey clashes with other mods are resolved automatically: when the Japanese patch, 邪神认知污染ON, 剧情随行 or any mod that sets the same key in its config file is detected, this mod unbinds its own hotkey and shows a notice once.

**Languages**

- The installer follows the Windows display language: Simplified or Traditional Chinese, English, Japanese or Korean.
- Release notes are available in English, Japanese and Korean from this version on; older versions are still shown in Chinese.
- In the Japanese UI, flag names, conditions, option results and the check records in the dialogue log are shown in Japanese.
- The Korean and Japanese UI also use the game's Source Han Serif font and follow the window font setting; the window title is translated too.

**Fixes**

- The Japanese UI didn't appear even with the Japanese patch installed, on PCs that had been running for less than 24.9 days.
- In the action and location menus in Jiangling and elsewhere, right-click didn't open the menu.

