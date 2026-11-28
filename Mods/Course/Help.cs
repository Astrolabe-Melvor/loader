namespace Astrolabe.Course;

static class Help
{
    public const string Text = """
[b]Course[/b] runs your actions one after another: do this until a goal, then that, then the next. The game runs one action at a time; Farming and Ranching keep going on their own next to it.

[b]Add a step[/b]
From the game: every skill and combat page has a [b]Queue[/b] button in its top-right corner. It opens this tab with that skill and the page's selected recipe (or what's running there) picked, so you only choose the goal.
Pick a skill and an action, then what "done" means: the bank holds an amount, you gain an amount, a skill reaches a level, it has been done a number of times, or time passes. Every list only offers what fits the action. The green line under the form says what the step will do; a gold line warns about anything that would stop it. [i]Copy what I'm doing[/i] fills in your current action. New steps go at the end, after the selected step, or at the top: pick it next to [i]Add step[/i].
[b]Fastest XP[/b] and [b]Most GP[/b] pick the best action for the skill and switch as you level. [i]Materials[/i] decides what they may use: [i]what's in the bank[/i]; [i]only actions that use nothing from the bank[/i] (gathering, thieving and the like); or [i]gather the ingredients itself[/i]. That last one ranks by GP or XP per hour of your own time, gathering counted in, and puts steps that gather about 15 minutes' worth of ingredients in front of it; when those run out it stops and gathers again. What you already had of each ingredient when the step started is never used, and finished gathering steps are cleared away each round.
[b]Sacrificial Anvil and Astral Offerings[/b] (Smithing; Beastcrafting weapons and armour): once unlocked in Astrology, a step that doesn't count items can use them (tick [i]Mode[/i]); Course turns the recipe's mode on when the step starts and back to how it was after. Steps that count items always run without them, since the anvil destroys what it makes. [i]Most GP[/i] uses the anvil by itself: it pays the item's sale value straight away, about 5% more on average than making and selling it. [i]Fastest XP[/i] uses it only when ticked (about 5% more XP, but nothing is kept).
[b]Slayer tasks[/b] (Combat): fights the highest master's task it can, moves to the next task when one is done, until a number of tasks, a level or a time. [i]Undo[/i] under the step list takes back the last change (an edit, a move, a removal).

[b]Thieving and Agility[/b]
[i]Fastest XP[/i] ranks targets by the game's live numbers: your success chance, stages, stun time and interval for thieving; each obstacle's chance and time for agility (a fail sends you back to the start), and it picks each course's best path. To run one path yourself, pick it by name ("Jump 3: Advanced Path"); "(best path)" picks for you. Targets that need a key or lockpick count only if you have one; arenas only if you can pay entry. Steps wear the gear you own that gives the most XP there, then the best loot (thieving) or least damage (agility), unless the step has its own toolbelt set. Food: the skill's best bonus food (sushi for stealth, soup for agility) stays selected; when HP drops below the percent in Options, or to where two failures could push it to the game's auto-eat threshold (so auto-eat never spends the sushi), it switches to healing food, eats up, and switches back. This runs while the game catches up on time away too. Options, under Thieving and agility: eat the cheapest per HP first (the food you'd give up the least gold for) or the biggest heal. [i]Your food[/i] lists it in groups that fold (click a group): what it heals with, bonus food kept on here, food saved for combat (tick one to heal with it too), and Kept out, where food you untick goes. Also there: what a queue step does when the healing food runs out (keep going, next step, make more first and come back, or do the idle action until there's food). Make more takes the amount and food you pick (or the cheapest per HP you can make), uses ingredients in the bank first or gathers them all and leaves your stock alone, and shows what it would do if a step ran out now.

[b]Train a technique[/b] (Agility only): picks the course and path giving that technique the most XP per hour and switches as it levels. Technique levels raise your chance on obstacles at or below their difficulty, so the ranking moves as you train. Goal: until the technique reaches a level. The step summary lists technique XP/h for the current path. All of this can be turned off in Options.

[b]Slayer tab[/b]
Every master's task, kills and costs. Click a master to extend, reroll or fight their task by hand, have Course extend their tasks as they come, or turn the game's Auto Slayer on or off. Under [i]Rules[/i]: a coin reserve rules never spend below, monsters to always reroll, rerolling tasks in areas you can't enter, and whether the rules run all the time or only during Slayer tasks steps.

[b]The step list[/b]
Tick the boxes to work on several steps at once: Up, Down, Remove and Duplicate work on them together, and dragging a ticked row drags them all. Drag any row to move it. Double-click a step to edit it; right-click it for Edit, Duplicate, Run from here, Move to the top or bottom, and Remove. [i]Remove finished[/i] clears done and skipped steps; [i]Remove all[/i] asks for a second press, and Undo brings them back. [i]Fold[/i] (right of the tabs) hides the open tab so the list gets the room.

[b]Plan an item[/b]
Choose any item and how many. The planner lists the steps to get it, including its ingredients, levels you need first, monsters that drop it, and shop purchases that sell it (a [i]Most GP[/i] step earns the GP first; a step then buys it). Currencies the queue can not earn for you (slayer coins, say) show in yellow. [i]Ingredients[/i]: use what's in the bank first, or gather them all and leave your stock alone. The add button puts every step in the queue where you pick (the end, after the selected step, or the top).

[b]Gear[/b]
Combat steps can wear one of your combat loadouts, hold a different weapon and fight in a set stance. [i]Best I own against it[/i] picks the weapon, offhand and stance that hit the monster hardest. With a slayer task monster, your slayer gauntlet goes on (Options).
Skill steps can put on a toolbelt set (Toolbelts tab). Toolbelt items an action needs, like a Filtered Vial for Warped Cacti, go in from the bank by themselves.

[b]Toolbelts[/b]
Make named sets of toolbelt items per skill. [i]On now[/i] shows what each slot has now, yellow where putting the set on would change it. A consumable with a refill amount is refilled when it runs out: from the bank, or by making or gathering more first, then the step carries on. Deleting a set asks for a second press; steps that used it leave the toolbelt as it is.

[b]Saved and shared[/b]
The top line shows the queue you have now and whether it changed since you saved it; [i]Save[/i] writes over the saved copy, [i]Save as new[/i] keeps another. The title bar's queue list says the same (a * means changed) and can save or load too. Your saved queues (shared by all your characters) list their steps on the right: [i]Load[/i] replaces your queue and stops it (Undo brings yours back), [i]Add to my queue[/i] puts its steps after yours, and there's Copy code, Rename and Delete. Paste a share code (CRS1:...) to see its steps first, then add them to your queue, replace your queue with them, or save them as a new queue. The wiki's planner makes the same codes, and "Send to Course" in Astrolabe Tools adds its steps to this queue on its own.

[b]History[/b]
What the queue did, by day, newest first. Days fold (click one); today and yesterday start open. Yellow lines need a look: click one to mark it seen (it fades and stops counting), or [i]Mark all seen[/i]. The History tab shows how many are left, as History (2). [i]Highlight[/i] picks what turns yellow: skipped steps, the queue pausing, and something that didn't work are on to start; running out of food or a refill, and the queue finishing, can be turned on. [i]Show[/i] can list only highlighted lines. [i]Copy[/i] copies what's shown. [i]Clear[/i] clears days before today, days older than a week, or all of it. Each character keeps the last 500, 2,000 (the default) or 10,000 lines.

[b]Options[/b]
Four sections on the left: [i]Queue[/i] (loop, take finished steps off, pause on a full bank, alerts), [i]Gear and combat[/i] (putting on each step's gear, with the default combat loadout and slayer gauntlet under it, and stopping combat when its food runs out), [i]Thieving and agility[/i] (best XP gear and food) and [i]Display[/i] (the small panel and the Queue button on game pages).

[b]Tours[/b]
The [b]Tour[/b] button has a short walk through the basics and one for each tab.

[b]While it runs[/b]
The small panel shows the current step: drag it anywhere, "–" folds it, click it to open Course. History lists everything the queue did. [i]When the queue waits or ends[/i] sets an action to do meanwhile.
The queue keeps going while the game catches up on time away: steps that finish move on, with the times in History, and the alerts come when you are back.
""";
}
