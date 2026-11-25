# Changelog

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
