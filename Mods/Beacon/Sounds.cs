using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace Astrolabe.Beacon;

/// <summary>
/// Alert sounds: the game's own notification and interface sounds, plus any .ogg, .wav or .mp3 put in
/// mods\Beacon\sounds. They play on Beacon's own audio bus with its own volume; optionally they are heard even while
/// the game mutes itself in the background.
/// </summary>
sealed class Sounds
{
    public const string None = "";
    public const string SkillOwn = "*skill";
    const string BusName = "AstrolabeAlerts";

    public sealed record Choice(string Id, string Name);

    readonly string _folder;
    readonly AudioStreamPlayer _player;
    readonly Dictionary<string, AudioStream?> _cache = new();
    List<Choice>? _list;
    int _playing;   // alerts playing with the game's mute lifted

    public Sounds(string modFolder, Node host)
    {
        _folder = Path.Combine(modFolder, "sounds");
        _player = new AudioStreamPlayer();
        host.AddChild(_player);
    }

    /// <summary>Every sound to pick from, game sounds first.</summary>
    public List<Choice> All()
    {
        if (_list != null) return _list;
        var list = new List<Choice>();
        foreach (string dir in new[] { "res://Assets/Audio/Notifications", "res://Assets/Audio/Interface" }) Scan(dir, list);
        try
        {
            Directory.CreateDirectory(_folder);
            foreach (string f in Directory.GetFiles(_folder).Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f))
                list.Add(new Choice("file:" + Path.GetFileName(f), "Yours: " + Path.GetFileNameWithoutExtension(f)));
        }
        catch { }
        return _list = list;
    }

    public void Rescan() { _list = null; _cache.Clear(); }

    // In an exported game the folder lists "name.wav.import"; the resource itself loads by its original path.
    static void Scan(string dir, List<Choice> into)
    {
        using DirAccess? d = DirAccess.Open(dir);
        if (d == null) return;
        foreach (string sub in d.GetDirectories()) Scan(dir + "/" + sub, into);
        foreach (string raw in d.GetFiles())
        {
            string f = raw.EndsWith(".import") ? raw[..^7] : raw.EndsWith(".remap") ? raw[..^6] : raw;
            if (!(f.EndsWith(".wav") || f.EndsWith(".ogg") || f.EndsWith(".mp3"))) continue;
            string id = dir + "/" + f;
            if (into.Any(c => c.Id == id)) continue;
            string group = dir[(dir.LastIndexOf('/') + 1)..];
            string name = Path.GetFileNameWithoutExtension(f);
            into.Add(new Choice(id, group == "SkillLevelUps" ? $"Game: {name} level up" : $"Game: {Spaced(name)}"));
        }
    }

    static string Spaced(string s) =>
        string.Concat(s.Select((c, i) => i > 0 && char.IsUpper(c) && char.IsLower(s[i - 1]) ? " " + c : c.ToString()));

    public string NameOf(string id) => id == None ? "none" : id == SkillOwn ? "the skill's own level-up sound" : All().FirstOrDefault(c => c.Id == id)?.Name ?? id;

    AudioStream? Load(string id)
    {
        if (_cache.TryGetValue(id, out AudioStream? hit)) return hit;
        AudioStream? s = null;
        try
        {
            if (id.StartsWith("res://")) s = ResourceLoader.Load<AudioStream>(id);
            else if (id.StartsWith("file:"))
            {
                string path = Path.Combine(_folder, id[5..]);
                if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) s = AudioStreamOggVorbis.LoadFromFile(path);
                else if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) s = AudioStreamWav.LoadFromFile(path);
                else if (path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) s = new AudioStreamMP3 { Data = File.ReadAllBytes(path) };
            }
        }
        catch { }
        return _cache[id] = s;
    }

    static int Bus()
    {
        int i = AudioServer.GetBusIndex(BusName);
        if (i >= 0) return i;
        AudioServer.AddBus();
        i = AudioServer.BusCount - 1;
        AudioServer.SetBusName(i, BusName);
        AudioServer.SetBusSend(i, "Master");
        return i;
    }

    /// <summary>Plays a sound. <paramref name="skillSound"/> is used for <see cref="SkillOwn"/>.</summary>
    public void Play(string id, AudioStream? skillSound, double volume, bool evenWhenMuted)
    {
        AudioStream? stream = id == SkillOwn ? skillSound : id == None ? null : Load(id);
        if (stream == null) return;
        int bus = Bus();
        AudioServer.SetBusVolumeDb(bus, (float)Mathf.LinearToDb(Math.Max(0.0001, volume)));
        _player.Bus = BusName;
        _player.Stream = stream;

        int master = AudioServer.GetBusIndex("Master");
        if (!evenWhenMuted || master < 0 || !AudioServer.IsBusMute(master) || _playing > 0)
        {
            _player.Play();
            return;
        }

        // The game muted itself (usually because it is in the background). Lift that for this one sound, with every
        // other game bus silenced so only the alert is heard, then put everything back.
        bool wasFocused = DisplayServer.WindowIsFocused();
        var silenced = new List<int>();
        for (int i = 0; i < AudioServer.BusCount; i++)
            if (i != master && i != bus && !AudioServer.IsBusMute(i)) { AudioServer.SetBusMute(i, true); silenced.Add(i); }
        AudioServer.SetBusMute(master, false);
        _playing++;
        _player.Play();
        _player.GetTree().CreateTimer(stream.GetLength() + 0.3).Timeout += () =>
        {
            _playing--;
            foreach (int i in silenced) if (i < AudioServer.BusCount) AudioServer.SetBusMute(i, false);
            // If the player came back to the game meanwhile, the game has set its own sound again; leave it.
            if (DisplayServer.WindowIsFocused() == wasFocused) AudioServer.SetBusMute(master, true);
        };
    }
}
