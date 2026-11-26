namespace Astrolabe.Course;

static class Help
{
    public const string Text = """
[b]Course[/b] runs your actions one after another: do this until a goal, then that, then the next. The game runs one action at a time; Farming and Ranching keep going on their own next to it.

[b]Add a step[/b]
From the game: every skill and combat page has a [b]Queue[/b] button in its top-right corner. It opens this tab with that skill and the page's selected recipe (or what's running there) picked, so you only choose the goal.
Pick a skill and an action, then what "done" means: the bank holds an amount, you gain an amount, a skill reaches a level, it has been done a number of times, or time passes. Every list only offers what fits the action. The green line under the form says what the step will do; a gold line warns about anything that would stop it. [i]Copy what I'm doing[/i] fills in your current action.
[b]Fastest XP[/b] and [b]Most GP[/b] pick the best action for the skill and switch as you level. [i]Materials[/i] decides what they may use: [i]what's in the bank[/i]; [i]only actions that use nothing from the bank[/i] (gathering, thieving and the like); or [i]gather the ingredients itself[/i]. That last one ranks by GP or XP per hour of your own time, gathering counted in, and puts steps that gather about 15 minutes' worth of ingredients in front of it; when those run out it stops and gathers again. What you already had of each ingredient when the step started is never used, and finished gathering steps are cleared away each round.
[b]Slayer tasks[/b] (Combat): fights the highest master's task it can, moves to the next task when one is done, until a number of tasks, a level or a time. [i]Undo[/i] under the step list takes back the last change (an edit, a move, a removal).

[b]Thieving and Agility[/b]
[i]Fastest XP[/i] ranks targets by the game's live numbers: your success chance, stages, stun time and interval for thieving; each obstacle's chance and time for agility (a fail sends you back to the start), and it picks each course's best path. To run one path yourself, pick it by name ("Jump 3: Advanced Path"); "(best path)" picks for you. Targets that need a key or lockpick count only if you have one; arenas only if you can pay entry. Steps wear the gear you own that gives the most XP there, then the best loot (thieving) or least damage (agility), unless the step has its own toolbelt set. Food: the skill's best bonus food (sushi for stealth, soup for agility) stays selected; when HP drops below the percent in Options, or to where one failure could push it to the game's auto-eat threshold (so auto-eat never spends the sushi), it switches to your biggest healing food, eats up, and switches back.

[b]Train a technique[/b] (Agility only): picks the course and path giving that technique the most XP per hour and switches as it levels. Technique levels raise your chance on obstacles at or below their difficulty, so the ranking moves as you train. Goal: until the technique reaches a level. The step summary lists technique XP/h for the current path. All of this can be turned off in Options.

[b]Slayer tab[/b]
Every master's task, kills and costs. Extend, reroll or fight a task by hand; turn the game's Auto Slayer on or off. Rules: extend a master's tasks as they come, always reroll monsters you list, reroll tasks in areas you can't enter, all keeping a coin reserve. Rules run during Slayer tasks steps, or all the time if you tick it.

[b]The step list[/b]
Tick the boxes to work on several steps at once: Up, Down and Remove move or remove them together, and dragging a ticked row drags them all. Drag any row to move it. Double-click a step to edit it. [i]Remove finished[/i] clears done and skipped steps; the option to take steps off the list as they finish is in Options.

[b]Plan an item[/b]
Choose any item and how many. The planner lists the steps to get it, including its ingredients, levels you need first, monsters that drop it, and shop purchases that sell it (a [i]Most GP[/i] step earns the GP first; a step then buys it). Currencies the queue can not earn for you (slayer coins, say) show in yellow. [i]Add these steps to the queue[/i] adds them all.

[b]Gear[/b]
Combat steps can wear one of your combat loadouts, hold a different weapon and fight in a set stance. [i]Best I own against it[/i] picks the weapon, offhand and stance that hit the monster hardest. With a slayer task monster, your slayer gauntlet goes on (Options).
Skill steps can put on a toolbelt set (Toolbelts tab). Toolbelt items an action needs, like a Filtered Vial for Warped Cacti, go in from the bank by themselves.

[b]Toolbelts[/b]
Make named sets of toolbelt items per skill. A consumable with "get up to" set is refilled when it runs out: from the bank, or by making or gathering more first, then the step carries on.

[b]Saved and shared[/b]
Save queues by name, or copy one as a code (CRS1:...) to share. The wiki's planner makes the same codes, and "Send to Course" in Astrolabe Tools adds its steps to this queue on its own.

[b]While it runs[/b]
The small panel shows the current step: drag it anywhere, "–" folds it, click it to open Course. History lists everything the queue did. [i]When the queue waits or ends[/i] sets an action to do meanwhile.
The queue keeps going while the game catches up on time away: steps that finish move on, with the times in History, and the alerts come when you are back.
""";
}
