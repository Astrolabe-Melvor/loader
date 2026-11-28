Extract the zips into the Melvor Idle 2 game folder (next to Melvor2.exe): the loader first, then any mods. `AstrolabeAll` has everything. Close the game first when updating; your settings are kept.

Already using Melvor Mod Loader? Keep it and extract only the mod zips you want. Astrolabe mods now run on either loader. Update all Astrolabe mods together with this release.

## 0.4.5
- Course 0.14.0: Sacrificial Anvil and Astral Offerings. Steps that don't count items can use them (tick "Mode"); Course turns them on for the step and back after. Most GP uses the anvil by itself (about 5% more GP than selling); Fastest XP when ticked.
- Course 0.14.0: thieving and agility food has its own Options section: eats the cheapest per HP first (or the biggest heal), a food table with heals, counts, GP per HP and bonuses, combat food saved for combat unless ticked, and what happens when a step runs out of healing food (keep going, next step, make more and come back, or idle until there's food). Make more can leave your bank stock alone and previews what it will make.
- Course 0.14.0: Options are split into four sections: Queue, Gear and combat, Thieving and agility, Display.
- Course 0.14.0: Saved and shared shows whether your queue changed since you saved it, lists saved queues with their steps, and previews a pasted share code before adding it (or replacing your queue, or saving it as new). Rename, Add to my queue, and undoable Load.
- Course 0.14.0: Add a step and Plan an item tidied up: Add step at the bottom, steps can go after the selected one or at the top, and Plan can gather every ingredient so your bank stock stays.
- Course 0.14.0: right-click a step for Edit, Duplicate, Move and Remove. Toolbelts show what's on now, Slayer groups its rules, History folds by day, highlights what needs a look until you've seen it (you pick what counts), can show only those lines, clears by age, and keeps a set number of lines.
- Course 0.14.0: the Tour button has a walk through each tab.
- Course 0.14.0: thieving and agility no longer eat the sushi as plain food: the food check now also runs while the game catches up on time away, heals a little earlier, and never heals with bonus food. With no plain food in the bank it tells you once.

## 0.4.4
- Course 0.13.0: Most GP and Fastest XP can now use only actions that need nothing from the bank, or gather their own ingredients first (self-funded, ranked by GP or XP per hour of your own time, and your existing stock is left alone). Pick it under "materials" when adding the step. Add a step also shows the top five picks right now, with their GP or XP per hour.

## 0.4.3 fixes
- Course 0.12.2: the planner data now downloads without Astrolabe Tools running. Before, Most GP, Fastest XP and Plan an item waited on "planner data not loaded" for most players. Steps above a "Run from here" no longer show as "done".

## 0.4.2 fixes
- Logbook 0.3.3: farm harvests, ranching, the grain mill, opened items and shop buys no longer show up as rare drops.

## 0.4.1 fixes
- Course 0.12.1: steps whose goal was already met show "already met" instead of a red "skipped"; clearer Agility best-path names.
- Hold 0.6.1 and Logbook 0.3.2: small wording fixes ("1 rule"; "idle" instead of 0 XP per hour).

## Course 0.12.0
- Runs while the game is closed: when the game catches up on time away, steps that reach their goal move on to the next. History shows when each happened; alerts play once you are back.
- A Queue button on every skill and combat page opens Add a step with that page's action already picked. Options can turn it off.

## Hold 0.6.0
- Bury all and Open all on the Bank tab. A confirm line shows what goes first. Locked stacks and the amounts your rules keep are left alone.

## Loader 0.4.0
- The window kit moved into each mod, so the mods no longer need Astrolabe Loader. Window settings now live in `mods\windows.json` (moved over automatically). The log is now `AstrolabeLoader.log` in the game folder.

Tested on game version 0.11.7. Found a bug? Mods menu (F1) > Report a bug.
