namespace Astrolabe.Anchor;

static class Help
{
    public const string Text = """
[b]Anchor[/b] keeps backups of your saves, so a mistake (or a bad mod) can be undone.

[b]Automatic[/b]: every character when the game starts, and the open character on a timer (30 minutes unless you change it). A save that has not changed is not backed up again. Anchor keeps the newest automatic backups and one a day for the last few days; older automatic ones are deleted.
[b]Back up now[/b]: a backup you make yourself, with an optional note ("before spending marks"). These are never deleted for you.
[b]Restore[/b]: from the title screen only (an open character would save over it). Pick a backup, click [i]Restore this backup[/i] twice. The current save is backed up first, so a restore can be undone. It goes through the game's own save code, so Steam Cloud gets the restored save too.
[b]Copy as export string[/b]: the same text as the game's own Export; its Import on the title screen accepts it.
A character deleted from the game still shows its backups here; restoring one brings it back.

Backups are in mods\Anchor\backups, one folder per character.
""";
}
