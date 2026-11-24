# Astrolabe Beacon

Tells you when something needs you. Built on Astrolabe Loader. **F8** sets it up (rebindable in the F1 Mods menu).

## Alerts

- A skill levels up
- You find a pet
- An item enters your bank for the first time ever on this character
- Combat food is running low (you pick how many is low) or runs out
- The bank is nearly full (you pick how many free slots)
- Nothing is running for a while (you pick how long)

Each one can show a note at the top of the screen, play its own sound, flash the taskbar when the game is in the background, and post to Discord. An alert fires once, then again only after its condition has cleared. Changes while the game catches up on time away are not alerted.

## Discord

Paste a channel webhook URL (Server settings > Integrations > Webhooks > Copy URL) and tick Discord for the alerts you want posted. The URL is kept on your computer only, in `mods\Beacon\options.json`.

## Sound

Pick a sound for each alert: any of the game's notification and interface sounds, a skill's own level-up sound, or your own (put .ogg, .wav or .mp3 files in `mods\Beacon\sounds` and press **Find new sounds**). ▶ plays it.

Beacon has its own volume, apart from the game's. With **Play alert sounds even while the game is muted in the background** on, an alert is heard even when the game mutes itself because it is not focused: for that one sound the game's mute is lifted with all of its other sounds silenced, then put back.
