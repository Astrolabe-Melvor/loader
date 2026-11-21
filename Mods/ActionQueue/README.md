# Astrolabe Action Queue

Do one action until a goal is reached, then the next. Built on Astrolabe Loader. **F6** opens it (rebindable in the F1 Mods menu).

## Tabs

- **Add a step:** pick a skill and an action (searchable, with icons), or "Use what I am doing now". "Fastest XP" trains the skill with the best action you can do, switching as you level. Goals: it stops by itself, I own N of an item, I get N more, a skill reaches a level, N minutes pass, done N times. "Sell from bank" sells an item down to an amount you keep (locked stacks are never sold).
- **Plan an item:** works back through the recipes to what must be gathered, picks the fastest action for each, and adds the steps. Steps that need a higher level get a "train the skill" step in front. Rates come from the Astrolabe wiki's `plan.json`, downloaded once a day and kept for offline use.
- **Saved and shared:** keep queues under a name; copy a queue as a share code or add steps from a pasted code. The Astrolabe wiki planner's "Copy for Action Queue" button makes these codes too.
- **History:** what the queue did, with times, kept between sessions.
- **Options:** start over at the end; sound and taskbar flash when it finishes, skips a step or pauses; stop combat when the food runs out; pause when the bank is full; corner panel.

## The table

Drag rows to reorder, double-click (or Edit) to change a step. Item goals show the time left at the current pace. While the queue runs the tabs fold away; click one to bring it back.

**Side by side (experimental):** the header button runs up to 4 lanes at once. Each step has a lane; each lane runs its steps in order. Two steps of the same skill cannot run at the same moment.

**Idle action:** what to do while every lane waits for materials, and after the queue ends.

The queue runs while the game is open. Offline progress keeps doing whatever was running when you closed the game.
