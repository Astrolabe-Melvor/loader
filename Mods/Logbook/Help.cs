namespace Astrolabe.Logbook;

static class Help
{
    public const string Text = """
[b]Logbook[/b] counts what you gain while you play.

[b]This session[/b]: XP per hour for each skill (last 10 minutes; hover for the whole session), levels gained, time to the next level, actions or kills, every item gained and used with its worth, and currencies.
[b]Notable drops[/b]: items new this session, and rare ones (at most once per 40 actions of what you were doing).
[b]Past sessions[/b]: the last 60, kept when you close the game or press [i]Start a new session[/i].
[b]Last time away[/b]: what changed while the game caught up on offline time. It is kept out of the per-hour rates.

The small panel shows your XP and GP per hour: drag it anywhere, "–" folds it, click it to open the Logbook.
""";
}
