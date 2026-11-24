namespace Astrolabe.Beacon;

static class Help
{
    public const string Text = """
[b]Beacon[/b] tells you when something needs you: a level up, a pet, a brand new item, low or no food, a nearly full bank, a character doing nothing, or (with Hold) a shop purchase bought or unlocked by your shop rules.

Each alert can show a note at the top of the screen, play its own sound at its own volume, flash the taskbar when the game is in the background, and post to Discord. [i]Test[/i] tries one. An alert fires once, then again only after its cause has cleared.

[b]Sound[/b]: pick any game sound, a skill's own level-up sound, or your own (.ogg, .wav or .mp3 in mods\Beacon\sounds, then [i]Find new sounds[/i]). Beacon has its own volume. With [i]Play alert sounds even while the game is muted in the background[/i] on, you hear alerts when the game is muted because it is not focused.

[b]Discord[/b]: in Discord, Server settings > Integrations > Webhooks > New Webhook > Copy URL, paste it here, and tick Discord for the alerts you want posted.
""";
}
