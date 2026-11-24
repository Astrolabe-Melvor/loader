# Astrolabe Anchor

Save backups. Built on Astrolabe Loader. **F4** opens it (rebindable in the F1 Mods menu), on the title screen too.

- **Automatic backups:** every character when the game starts, and the open character every 30 minutes (adjustable). Unchanged saves are skipped. Keeps the newest 20 automatic backups plus one a day for 7 days; both adjustable.
- **Back up now**, with an optional note. Backups you make are never pruned.
- **Restore** from the title screen (two clicks). The current save is backed up first, so a restore can be undone. Restores go through the game's own save code, so Steam Cloud gets them too.
- **Copy as export string:** the same text as the game's Export, accepted by its Import.
- Characters deleted from the game keep their backups; restoring one brings the character back.

Backups are plain text files (the game's export string) in `mods\Anchor\backups\<save id>\`, with an `index.json` of times, levels, GP and notes.
