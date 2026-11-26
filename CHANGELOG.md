# Changelog

## 0.4.3
- Course 0.12.2: the planner data (used by Most GP, Fastest XP and Plan an item) never downloaded unless Astrolabe Tools was running: the download failed with "This instance has already started one or more requests". Fixed. Most GP and Fastest XP steps also load the planner data themselves. Before, a queue that started without the Course window being opened that session waited forever on "planner data not loaded". The waiting message now says why (downloading, or the download error). Steps above a "Run from here" now read "not run" or "stopped early" (if it was running) instead of "done", aren't counted as done, and stay when you click Remove finished.
- Loader 0.4.3: no changes; the release number moves with the mods.

## 0.4.2
- Logbook 0.3.3: farm harvests, ranching, the grain mill, opened items and shop buys no longer show up as rare drops. Only what your current action itself gives can be rare.
- Loader 0.4.2: no changes; the release number moves with the mods.
- Leaves out createdump.exe from the .NET runtime (unused; mod sites quarantine any .exe).

## 0.4.1
- Course 0.12.1: a step whose goal was already met shows "already met" instead of a red "skipped". Agility best-path steps read "Train Agility (Jump 3: Simple Path)". The Add a step form says "Your character is doing" instead of "Right now", which read as the step's pick.
- Hold 0.6.1: "1 rule", "1 stack" instead of "1 rules", "1 stacks".
- Logbook 0.3.2: a skill with no XP in the last 10 minutes shows "idle" in XP per hour instead of 0.
- Loader 0.4.1: no changes; the release number moves with the mods.

## 0.4.0
- Every mod now runs on Astrolabe Loader or on Melvor Mod Loader (falorfrozen's). The window kit is built into each mod instead of the loader, so update the loader and all mods together. Window sizes, positions, scale and tours move to `mods\windows.json`. The loader's log is now `AstrolabeLoader.log` in the game folder.
- Course 0.12.0: the queue keeps going while the game catches up on time away; steps that reach their goal move on, History gets the time each happened, alerts play when you are back. A Queue button on every skill and combat page opens Add a step with that page's action picked (can be turned off in Options). The old Action Queue folder is only moved when it is ours.
- Hold 0.6.0: Bury all and Open all on the Bank tab, with a confirm line listing what goes. Locked stacks and the amounts your rules keep are left alone.
- Logbook 0.3.1, Beacon 0.4.1, Spyglass 0.2.1, Anchor 0.2.1, Deckhand 0.2.1: built with the kit inside.

## 0.3.1
- Hold 0.5.0: Cleanup tab: spare gear, outgrown gear and unused items in your bank, to sell now or give a rule. Same list as Astrolabe Tools' Bank cleanup.
- Loader 0.3.1: the tour is offered by a green "Take the tour" button instead of a bar that pushed every window's content down; tables shrink to fit the screen before a window scrolls; one card per mod in the Mods menu; Check now shows its result; Theme preview hidden (mod makers: put a file named `dev` in the mods folder).

## Loader 0.3.0
- Mods menu shows each mod's version and health: loaded, did not load, paused after errors, or calling game code that a game update removed.
- Daily update check against the newest GitHub release (only the release list is read). Get updates opens the release page.
- Report a bug: versions, mods, errors and the end of the log in one report, copied, saved to `mods\bug-reports\`, or pasted into a GitHub issue. Your Windows user name is taken out of the log.
- Game update check: the top-left tag says when the game updated, and mods are checked for game calls that are gone.
- A frame handler that keeps failing is paused after 5 errors in a minute instead of erroring every frame, with a note on screen.
- Window tours: a Tour button in every mod window's title bar, offered once the first time a window opens.
- Windows remember where you drag them. "Fit all windows again" also centres them.
- Tables refill in place, so scrolling and selection stay put.

## Course 0.11.0
- Agility: each path of a course can be picked ("Jump 3: Advanced Path"), or "(best path)". Before, the Simple Path always ran.
- Tick several steps to move, drag or remove them together. Tick all, Remove finished.
- Option to take steps off the list once they reach their goal.
- The State column grows with the window, long waits show in days, and hovering a state shows all of it.
- Agility steps can run until the bank holds (or you gain) a number of Agility Marks or other obstacle drops.
- Queue alerts go through Beacon when it is installed.
- Tour.

## Logbook 0.3.0
- Tables no longer jump back to the top every refresh. "Only rare drops" applies straight away.
- Rare drops are sent to Beacon.
- Tour.

## Beacon 0.4.0
- New alerts: Course queue (finished, skipped a step, paused) and rare drops from Logbook.
- Tour.

## Hold 0.4.0, Spyglass 0.2.0, Anchor 0.2.0, Deckhand 0.2.0
- Tours. Spyglass downloads fresh item data after a game update.
