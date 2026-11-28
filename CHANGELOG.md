# Changelog

## 0.4.5
- Course 0.14.0: Sacrificial Anvil (Smithing) and Astral Offerings (Beastcrafting weapons and armour). Steps that don't count items get a "mode" tick to use them once unlocked; Course turns the recipe's mode on when the step starts and puts it back after. Steps that count items always run with the mode off, since it would destroy or replace what they count. Most GP uses the anvil by itself and ranks smithing by the anvil's pay (about 5% over selling); Fastest XP uses it only when ticked, and ranks with its extra XP.
- Course 0.14.0: thieving and agility food in Options gets its own section: one line saying what it will heal with and keep on, the eating order (cheapest per HP first, the default, or the biggest heal), and a food list with each food's heal, count, GP per HP and bonus, in groups that fold; unticked food moves to its own folded Kept out group. Plain food heals; thieving and agility bonus food is only kept on for its bonus; combat bonus food is saved for combat unless ticked. Pick what a queue step does when the healing food runs out: keep going (as before), stop and go to the next step, make more (a food you pick, or the cheapest per HP you can make; planned like Plan an item, with ingredients from the bank first or all gathered so your stock stays; a preview line says what it would make, how, and about how long) and come back, or do the idle action until there is food again and come back. Eating moves to the next food when one runs out.
- Course 0.14.0: Options are split into four sections picked on the left: Queue, Gear and combat, Thieving and agility, Display. Settings that depend on another sit under it and dim when it's off. Each has a short name with a line of detail under it.
- Course 0.14.0: Saved and shared is redone. The top line shows the queue you have now, its step count, and "changed since you saved it"; Save writes over the saved copy, Save as new keeps another. Saved queues are listed with their step count and save date, and the picked one shows its steps, loop and idle action, with Load (now undoable: Undo puts back your steps, name, loop and idle action), Add to my queue, Copy code, Rename and Delete (asks twice). Saving over another saved queue asks twice. Share codes: paste one and see its steps (and anything left out) before anything changes, then add them to your queue, replace it (undoable), or save them as a new queue; Copy my queue as a code makes one. The title bar's queue list marks a changed queue with * and has Save changes.
- Course 0.14.0: Add a step: labels line up, Add step moves to the bottom after every choice, and new steps can go at the end, after the selected step, or at the top. The target box no longer stretches across the row. Mode is a short tick with its detail beside it. A skill with no toolbelt sets says so with a Make a set button that opens Toolbelts on that skill; Edit sets opens it on the skill too. Combat shows each style's weakest stance on one line (every stance on hover, one style per line) and the best weapon on the next. The idle picker follows the queue when loading or Undo changes it. Fishing spots, agility courses and dungeons show their skill's icon instead of the game's stand-in picture. The agility summary no longer ends in an empty "technique XP/h: )".
- Course 0.14.0: Plan an item: an Ingredients choice (bank first, or gather them all and leave your stock alone), the total on its own line, a short key for blue and yellow lines, a hint while nothing is picked, and steps go where you pick (end, after the selected step, top). The button says how many steps it adds, and the tab stays open after adding.
- Course 0.14.0: the step list: Duplicate (button and right-click), a right-click menu (Edit, Duplicate, Run from here, Move to the top or bottom, Remove), Remove all asks for a second press, a Fold button beside the tabs, and clicking the open tab folds it any time (before, only while running). The idle line only shows when no idle action is set.
- Course 0.14.0: Toolbelts: an On now column (yellow where the set would change a slot), "used by N steps", and Delete set asks twice. Slayer: the master's buttons sit under the table and the first master is picked; the coin reserve, monsters to reroll and the two rule ticks are together under Rules. History: days fold (today and yesterday open), Open all and Fold all. Highlighted lines need a look until you click them (or Mark all seen), and the tab shows how many are left, as History (2). Highlight picks what's highlighted: skipped steps, pauses and things that didn't work (on to start), running out of food or a refill, and the queue finishing. A filter shows only highlighted lines; Copy, and Clear for days before today, older than a week, or everything. History keeps the last 500, 2,000 (default) or 10,000 lines; the file on disk used to grow forever and is now cut back to that.
- Course 0.14.0: Tour has a walk through the basics and one for each tab (Add a step, Plan an item, Saved and shared, Toolbelts, Slayer, History, Options).
- Course 0.14.0: sushi fix. Sushi and other bonus food was still eaten as plain food in two cases: while the game caught up on time away (the food check only ran while the game was open), and when the bank had no plain food (the heal fell back to the sushi itself). The check now runs during time away too, once a game second, and History sums up what it ate when you're back. It never eats bonus food to heal; with no plain food in the bank it says so once. It also heals earlier: when two failures in a row could reach the game's auto-eat point, not one.
- Loader 0.4.5: windows with several tours list them under the Tour button; a first visit still goes straight to the first one.

## 0.4.4
- Course 0.13.0: Most GP and Fastest XP have a Materials choice: use what's in the bank (as before), only actions that use nothing from the bank, or gather the ingredients itself. Self-funded steps rank by GP or XP per hour of your own time with gathering counted in, gather about 15 minutes' worth at a time in steps put in front of them, and never use the stock you already had. Add a step lists the top five picks right now with their GP or XP per hour, so you can see what it will choose. Actions that use up a toolbelt item (meteorite ore and its sightings log) count as using the bank, so the other two choices skip them. With the bank choice, Most GP and Fastest XP now also consider actions that only need a toolbelt item you have in the bank (before, they had to be in the toolbelt already); the step puts it in.
- Planner data: fishing spots, thieving targets, potions and a few hunting animals had the wrong level (their area's, not their own), so Plan an item could plan actions you can't do yet. Fixed in the wiki's data; Course picks it up on its next daily download.
- Loader 0.4.4: no changes; the release number moves with the mods.

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
