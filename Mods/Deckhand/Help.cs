namespace Astrolabe.Deckhand;

static class Help
{
    public const string Text = """
[b]Deckhand[/b] looks after the skills that run on their own, checking every few seconds while you play (not during offline catch-up).

[b]Ranching[/b]: pets every animal that wants it, and repairs an enclosure once its maintenance drops below the percent you set, never taking GP below your reserve. If it can't (missing logs or bars, say) it says what is short, here and through Beacon. [i]Pet all now[/i] and [i]Repair all now[/i] do it straight away.
[b]Grain mill[/b]: pick what it makes (switching stops it, changes product and starts it again, as the game asks); it puts grain from the bank into the mill (per grain: whether to, and how many to leave in the bank), and starts it again when it stopped because it ran out. A mill you stopped yourself stays stopped.
[b]Farming[/b]: the game's notices that automation or auto-plant can't be paid for, a harvest found the bank full, or compost ran out go to Beacon (if installed) as alerts, at most once every 10 minutes each.
Everything Deckhand does is listed at the bottom. Settings are per character.
""";
}
