using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Astrolabe.Spyglass;

/// <summary>One line of an item's sources, uses or unlocks: what it is, its name, the skill it belongs to.</summary>
public sealed record Ref(string Type, string Id, string Name, string Skill, double? Min = null, double? Max = null, double? Chance = null);

public sealed record Entry(string Name, List<Ref> Sources, List<Ref> Uses, List<Ref> Unlocks);

/// <summary>
/// Item sources, uses and unlocks, from Astrolabe Tools when it runs on this PC, else the wiki's spyglass.json
/// (kept in mods\Spyglass for offline use, refreshed once a day).
/// </summary>
public sealed class Lookup
{
    public const string ToolsUrl = "http://127.0.0.1:5320/api/spyglass";
    public const string WikiUrl = "https://astrolabe-melvor.github.io/spyglass.json";

    public Dictionary<string, Entry> Items { get; } = new();
    public string GameVersion { get; private set; } = "";

    static Lookup? s_data;
    static Task? s_loading;
    public static string State { get; private set; } = "not loaded";

    /// <summary>The data if ready; starts loading it otherwise.</summary>
    public static Lookup? Get(string folder, Action<string> log)
    {
        if (s_data != null || (s_loading != null && !s_loading.IsCompleted)) return s_data;
        string file = Path.Combine(folder, "spyglass.json");
        if (File.Exists(file)) s_data = Parse(File.ReadAllText(file), log);
        bool stale = s_data == null || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1);
        if (!stale) { State = "ready"; return s_data; }
        s_loading = Task.Run(async () =>
        {
            using var http = new HttpClient();
            foreach (string url in new[] { ToolsUrl, WikiUrl })
            {
                try
                {
                    State = url == ToolsUrl ? "asking Astrolabe Tools" : "downloading from the wiki";
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(url == ToolsUrl ? 2 : 30));
                    string json = await http.GetStringAsync(url, cts.Token);
                    Lookup? got = Parse(json, log);
                    if (got == null) continue;
                    s_data = got;
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(file, json);
                    State = "ready";
                    return;
                }
                catch { }
            }
            State = s_data != null ? "ready (offline copy)" : "could not download the item data";
        });
        return s_data;
    }

    static Lookup? Parse(string json, Action<string> log)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var d = new Lookup { GameVersion = doc.RootElement.TryGetProperty("gameVersion", out var v) ? v.GetString() ?? "" : "" };
            foreach (JsonProperty item in doc.RootElement.GetProperty("items").EnumerateObject())
            {
                JsonElement o = item.Value;
                d.Items[item.Name] = new Entry(o.GetProperty("n").GetString() ?? item.Name, Rows(o, "s"), Rows(o, "u"), Rows(o, "r"));
            }
            return d;
        }
        catch (Exception e) { log("Item data unreadable: " + e.Message); return null; }
    }

    static double? Num(JsonElement row, int i) =>
        row.GetArrayLength() > i && row[i].ValueKind == JsonValueKind.Number ? row[i].GetDouble() : null;

    static List<Ref> Rows(JsonElement o, string key)
    {
        var list = new List<Ref>();
        if (!o.TryGetProperty(key, out JsonElement arr)) return list;
        foreach (JsonElement r in arr.EnumerateArray())
        {
            string S(int i) => r.GetArrayLength() > i && r[i].ValueKind == JsonValueKind.String ? r[i].GetString() ?? "" : "";
            // Sources: type, id, name, skill, min, max, chance. Uses: type, id, name, skill, qty.
            list.Add(key == "s" ? new Ref(S(0), S(1), S(2), S(3), Num(r, 4), Num(r, 5), Num(r, 6))
                : key == "u" ? new Ref(S(0), S(1), S(2), S(3), Num(r, 4))
                : new Ref(S(0), S(1), S(2), S(3)));
        }
        return list;
    }

    /// <summary>A plain word for the kind of thing a line is.</summary>
    public static string Kind(Ref r) => r.Type switch
    {
        "Enemy" => "Monster drop",
        "Dungeon" => "Dungeon",
        "Item" => "Opened from",
        "ShopPurchase" => "Shop",
        "Quest" => "Quest reward",
        "ArtisanRecipe" or "SmithingRecipe" or "CookingRecipe" or "FletchingRecipe" or "CraftingRecipe" or "HerbloreRecipe" or "RunecraftingRecipe" => "Made with " + (r.Skill.Length > 0 ? r.Skill : "a skill"),
        _ when r.Type.EndsWith("Recipe") || r.Type.EndsWith("Enclosure") || r.Type.EndsWith("Distraction") => r.Skill.Length > 0 ? r.Skill : r.Type,
        _ => r.Skill.Length > 0 ? r.Skill : r.Type,
    };
}
