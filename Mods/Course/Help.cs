namespace Astrolabe.Course;

static class Help
{
    public const string Text = """
[b]Course[/b] runs your actions one after another: do this until a goal, then that, then the next. The game runs one action at a time; Farming and Ranching keep going on their own next to it.

[b]Add a step[/b]
Pick a skill and an action, then what "done" means: the bank holds an amount, you gain an amount, a skill reaches a level, it has been done a number of times, or time passes. Every list only offers what fits the action. The green line under the form says what the step will do; a gold line warns about anything that would stop it. [i]Copy what I'm doing[/i] fills in your current action.
[b]Fastest XP[/b] and [b]Most GP[/b] pick the best action for the skill and switch as you level.
[b]Slayer tasks[/b] (Combat): fights the highest master's task it can, moves to the next task when one is done, until a number of tasks, a level or a time. [i]Undo[/i] under the step list takes back the last change (an edit, a move, a removal).

[b]Slayer tab[/b]
Every master's task, kills and costs. Extend, reroll or fight a task by hand; turn the game's Auto Slayer on or off. Rules: extend a master's tasks as they come, always reroll monsters you list, reroll tasks in areas you can't enter, all keeping a coin reserve. Rules run during Slayer tasks steps, or all the time if you tick it.

[b]Plan an item[/b]
Choose any item and how many. The planner lists the steps to get it, including its ingredients, levels you need first, monsters that drop it, and shop purchases that sell it (a [i]Most GP[/i] step earns the GP first; a step then buys it). Currencies the queue can not earn for you (slayer coins, say) show in yellow. [i]Add these steps to the queue[/i] adds them all.

[b]Gear[/b]
Combat steps can wear one of your combat loadouts, hold a different weapon and fight in a set stance. [i]Best I own against it[/i] picks the weapon, offhand and stance that hit the monster hardest. With a slayer task monster, your slayer gauntlet goes on (Options).
Skill steps can put on a toolbelt set (Toolbelts tab). Toolbelt items an action needs, like a Filtered Vial for Warped Cacti, go in from the bank by themselves.

[b]Toolbelts[/b]
Make named sets of toolbelt items per skill. A consumable with "get up to" set is refilled when it runs out: from the bank, or by making or gathering more first, then the step carries on.

[b]Saved and shared[/b]
Save queues by name, or copy one as a code (CRS1:...) to share. The wiki's planner makes the same codes.

[b]While it runs[/b]
The small panel shows the current step: drag it anywhere, "–" folds it, click it to open Course. History lists everything the queue did. [i]When the queue waits or ends[/i] sets an action to do meanwhile.
""";
}
