using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.Actions;
using GamesByMalcs.Melvor2.Components.CombatFood;
using GamesByMalcs.Melvor2.Components.Experience;
using GamesByMalcs.Melvor2.Entities;
using GamesByMalcs.Melvor2.Entities.Combat;
using GamesByMalcs.Melvor2.EventBus;
using MelvorModLoader;

namespace Astrolabe.Beacon;

public enum Kind { LevelUp, Pet, NewItem, LowFood, NoFood, BankFull, Idle, ShopBought, ShopUnlocked, Farming }

/// <summary>How one kind of alert is delivered.</summary>
public sealed class Rule
{
    public bool Show { get; set; } = true;      // a note on screen
    /// <summary>Sound to play: null = this alert's default, "" = none, "*skill" = the skill's own level-up sound,
    /// "res://..." = a game sound, "file:name.ogg" = one from mods\Beacon\sounds.</summary>
    public string? Chime { get; set; }
    public bool Flash { get; set; } = true;     // flash the taskbar when the game is in the background
    public double Volume { get; set; } = 1.0;   // share of the Beacon volume, 0 to 1
    public bool Discord { get; set; }           // post to the webhook
}

public sealed class Options
{
    public Dictionary<Kind, Rule> Rules { get; set; } = new();
    public int LowFood { get; set; } = 20;      // warn at this many of the selected food
    public int BankSlots { get; set; } = 5;     // warn when this many bank slots are left
    public int IdleSeconds { get; set; } = 60;  // warn after this long with nothing running
    public string Webhook { get; set; } = "";
    public double Volume { get; set; } = 0.8;   // Beacon's own volume, apart from the game's
    public bool EvenWhenMuted { get; set; } = true;   // heard even while the game mutes itself in the background

    public Rule Of(Kind k)
    {
        if (!Rules.TryGetValue(k, out Rule? r))
            Rules[k] = r = new Rule { Discord = k is Kind.LevelUp or Kind.Pet };
        return r;
    }
}

public sealed record Alert(DateTime Time, Kind Kind, string Text, string Skill = "", bool Test = false);

/// <summary>Checks the game once a second and raises alerts. Each alert fires once until its condition clears.</summary>
public sealed class Watcher
{
    static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static readonly Dictionary<Kind, string> Names = new()
    {
        [Kind.LevelUp] = "A skill levels up",
        [Kind.Pet] = "You find a pet",
        [Kind.NewItem] = "An item enters your bank for the first time ever",
        [Kind.LowFood] = "Combat food is running low",
        [Kind.NoFood] = "Combat food runs out",
        [Kind.BankFull] = "The bank is nearly full",
        [Kind.Idle] = "Nothing is running",
        [Kind.ShopBought] = "Hold buys something by your shop rules",
        [Kind.ShopUnlocked] = "A shop purchase you have a rule for unlocks",
        [Kind.Farming] = "Farming or ranching needs you (from Deckhand: can't pay automation or a repair, bank full at harvest, no compost)",
    };

    readonly string _folder;
    readonly Action<string> _log;
    readonly Dictionary<string, int> _levels = new();
    readonly HashSet<Kind> _active = new();     // conditions currently true (fire once until they clear)
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _pets = new();
    HashSet<string> _seen = new();
    string _character = "";
    Game? _hooked;
    double _next, _idleSince = -1;
    bool _seenDirty;

    public Options Options { get; private set; }
    public List<Alert> Recent { get; } = new();
    public event Action<Alert, Rule>? Raised;

    public Watcher(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
        Options = Read<Options>(Path.Combine(folder, "options.json")) ?? new Options();
    }

    public void Tick(Game? game, double now)
    {
        if (game == null) { _character = ""; _idleSince = -1; return; }
        if (game.CharacterName != _character) Begin(game);
        Hook(game);
        if (GameAccess.IsComputingOffline) { _idleSince = -1; return; }   // catching up: changes here are not news
        if (now < _next) return;
        _next = now + 1;

        while (_pets.TryDequeue(out string? pet)) Raise(Kind.Pet, $"Found a pet: {pet}");

        // Level ups.
        foreach (GameEntity e in game.LeveledEntities.OfType<GameEntity>())
        {
            int lv = e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault()?.Level ?? 0;
            if (_levels.TryGetValue(e.Id, out int was) && lv > was) Raise(Kind.LevelUp, $"{e.DisplayName} reached level {lv}", e.Id);
            _levels[e.Id] = lv;
        }

        ActionComponent? running = game.ActionManager.ActiveActions.FirstOrDefault();
        ActionComponent? any = running ?? game.Entities.GetAllObjects().SelectMany(x => x.Components.Values).OfType<ActionComponent>().FirstOrDefault();

        // Items seen for the first time ever on this character.
        if (any != null)
        {
            bool first = _seen.Count == 0;
            foreach (var st in any.Inventory.Stacks)
            {
                if (st.IsPlaceholder || !_seen.Add(st.Item.Id)) continue;
                _seenDirty = true;
                if (!first) Raise(Kind.NewItem, $"New item: {st.Item.DisplayName}");
            }
            if (_seenDirty) { _seenDirty = false; Write(SeenFile, _seen); }

            // Bank space.
            int left = any.Inventory.MaxStacks - any.Inventory.StackCount;
            Condition(Kind.BankFull, any.Inventory.MaxStacks > 0 && left <= Options.BankSlots,
                left <= 0 ? "The bank is full" : $"The bank has {left} free slot{(left == 1 ? "" : "s")} left");
        }

        // Food, while fighting.
        if (running is CombatActionComponent && running.Entity.Components.Values.OfType<FoodComponent>().FirstOrDefault() is FoodComponent food)
        {
            int n = food.SelectedFood == null ? 0 : food.Inventory.GetQuantity(food.SelectedFood.Item);
            string what = food.SelectedFood?.Item.DisplayName.ToString() ?? "food";
            Condition(Kind.NoFood, n <= 0, food.SelectedFood == null ? "No food selected for combat" : $"Out of {what}");
            Condition(Kind.LowFood, n > 0 && n <= Options.LowFood, $"Only {n} {what} left");
        }
        else { _active.Remove(Kind.NoFood); _active.Remove(Kind.LowFood); }

        // Nothing running for a while.
        if (running != null) _idleSince = -1;
        else if (_idleSince < 0) _idleSince = now;
        Condition(Kind.Idle, _idleSince >= 0 && now - _idleSince >= Options.IdleSeconds, "Your character is not doing anything");
    }

    /// <summary>Raises once when the condition turns true; again only after it has cleared.</summary>
    void Condition(Kind k, bool on, string text)
    {
        if (!on) { _active.Remove(k); return; }
        if (_active.Add(k)) Raise(k, text);
    }

    void Raise(Kind k, string text, string skill = "", bool test = false)
    {
        var a = new Alert(DateTime.Now, k, text, skill, test);
        Recent.Insert(0, a);
        if (Recent.Count > 100) Recent.RemoveAt(Recent.Count - 1);
        Rule r = Options.Of(k);
        if (r.Discord) Post(text, k, Context());
        Raised?.Invoke(a, r);
    }

    static readonly Dictionary<Kind, (string Title, int Color)> s_look = new()
    {
        [Kind.LevelUp] = ("Level up", 0x8FD46F),
        [Kind.Pet] = ("Pet found", 0xC58CF0),
        [Kind.NewItem] = ("New item", 0x8EC9EE),
        [Kind.LowFood] = ("Food running low", 0xF0C348),
        [Kind.NoFood] = ("Out of food", 0xEC7F6C),
        [Kind.BankFull] = ("Bank nearly full", 0xF0C348),
        [Kind.Idle] = ("Doing nothing", 0xA7A49B),
        [Kind.ShopBought] = ("Bought from the shop", 0x8FD46F),
        [Kind.ShopUnlocked] = ("Shop purchase unlocked", 0x8EC9EE),
        [Kind.Farming] = ("Farming or ranching needs you", 0xF0C348),
    };

    /// <summary>What the character is up to, for the Discord post.</summary>
    List<object> Context()
    {
        var fields = new List<object>();
        Game? game = _hooked;
        if (game == null) return fields;
        try
        {
            ActionComponent? running = game.ActionManager.ActiveActions.FirstOrDefault();
            fields.Add(new { name = "Doing", value = running == null ? "nothing" : running.Entity.DisplayName.ToString(), inline = true });
            ActionComponent? any = running ?? game.Entities.GetAllObjects().SelectMany(x => x.Components.Values).OfType<ActionComponent>().FirstOrDefault();
            if (any != null && any.Inventory.MaxStacks > 0)
                fields.Add(new { name = "Bank", value = $"{any.Inventory.StackCount} / {any.Inventory.MaxStacks}", inline = true });
            int total = game.LeveledEntities.OfType<GameEntity>().Sum(e => e.Components.Values.OfType<ExperienceComponent>().FirstOrDefault()?.Level ?? 0);
            fields.Add(new { name = "Total level", value = total.ToString("N0"), inline = true });
        }
        catch { }
        return fields;
    }

    /// <summary>Posts to the Discord webhook, if one is set: an embed coloured by the kind of alert, with what the
    /// character is doing. Runs in the background; failures go to the log.</summary>
    public void Post(string text, Kind? kind = null, List<object>? fields = null)
    {
        string url = Options.Webhook.Trim();
        if (!url.StartsWith("https://")) return;
        var (title, color) = kind is Kind k && s_look.TryGetValue(k, out var look) ? look : ("Astrolabe Beacon", 0x2F8F9D);
        var embed = new
        {
            author = new { name = _character.Length > 0 ? _character : "Melvor Idle 2" },
            title,
            description = text,
            color,
            fields = fields ?? new List<object>(),
            footer = new { text = "Astrolabe Beacon" },
            timestamp = DateTime.UtcNow.ToString("o"),
        };
        string body = JsonSerializer.Serialize(new { embeds = new[] { embed } });
        Task.Run(async () =>
        {
            try
            {
                using var res = await s_http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                if (!res.IsSuccessStatusCode) _log($"Discord answered {(int)res.StatusCode}");
            }
            catch (Exception e) { _log("Discord: " + e.Message); }
        });
    }

    /// <summary>An alert another mod asked for (Hold's shop alerts), sent over the mod message bus.</summary>
    public void FromMod(Kind k, string text)
    {
        if (_character.Length == 0 || GameAccess.IsComputingOffline) return;
        Raise(k, text);
    }

    public void Test(Kind k, string skill = "") => Raise(k, "Test: " + Names[k], skill, test: true);

    void Begin(Game game)
    {
        _character = game.CharacterName;
        _levels.Clear();
        _active.Clear();
        _idleSince = -1;
        _seen = Read<HashSet<string>>(SeenFile) ?? new();
    }

    void Hook(Game game)
    {
        if (_hooked == game) return;
        _hooked = game;
        try
        {
            var bus = typeof(Game).GetProperty("EventBus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(game) as GameEventBus;
            // May be raised on the offline thread: queue it, report it on the next check.
            bus?.PetUnlocked.Subscribe((_, e) => _pets.Enqueue(e.ActivePet.Pet.DisplayName.ToString()));
        }
        catch (Exception e) { _log("Pet alerts unavailable: " + e.Message); }
    }

    string SeenFile
    {
        get
        {
            string name = _character;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return Path.Combine(_folder, "seen", name + ".json");
        }
    }

    public void SaveOptions() => Write(Path.Combine(_folder, "options.json"), Options);

    void Write<T>(string file, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(value));
        }
        catch (Exception e) { _log("Could not save: " + e.Message); }
    }

    static T? Read<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file)) : null; }
        catch { return null; }
    }
}
