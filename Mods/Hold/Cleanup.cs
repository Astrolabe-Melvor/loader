using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace Astrolabe.Hold;

/// <summary>One bank item the cleanup suggests selling: "spare", "outgrown" or "unused", and how many to keep.</summary>
public sealed record CleanRow(Row Bank, string Group, long Keep, string Why)
{
    public long Sell => Math.Max(0, Bank.Qty - Keep);
    public long Worth => Sell * Bank.Price;
}

/// <summary>
/// What in the bank is safe to sell, the same as Astrolabe Tools' Bank cleanup: spare copies of gear you can only wear
/// one of (two rings), gear something else you own beats on every stat, and items nothing in the game uses.
/// What each item is for comes from cleanup.json (Astrolabe Tools when it runs on this PC, else the wiki), downloaded
/// once a day; what you own comes from the game.
/// </summary>
static class Cleanup
{
    public const string ToolsUrl = "http://127.0.0.1:5320/api/cleanup-facts";
    public const string WikiUrl = "https://astrolabe-melvor.github.io/cleanup.json";

    sealed class Fact
    {
        public string N = "";
        public long P;
        public int G, K = 1;
        public bool U, X, B, E;
        public string C = "";
        public Dictionary<string, double> S = new();
    }

    static Dictionary<string, Fact>? s_facts;
    static Task? s_loading;
    static bool s_force;
    static string s_folder = "";
    static Action<string> s_log = _ => { };
    static DateTime s_lastTry;

    /// <summary>Tries again when the data is missing: right away when asked, else at most once a minute.</summary>
    public static void Retry(bool now)
    {
        if (s_facts != null || s_folder.Length == 0) return;
        if (now || DateTime.UtcNow - s_lastTry > TimeSpan.FromMinutes(1)) Load(s_folder, s_log);
    }

    public static bool Loading => s_loading != null && !s_loading.IsCompleted;
    public static string State { get; private set; } = "not loaded";
    public static bool Ready => s_facts != null;

    /// <summary>Downloads the data again even if the copy here is recent (after a game update).</summary>
    public static void Refresh(string folder, Action<string> log)
    {
        s_force = true;
        Load(folder, log);
    }

    /// <summary>Loads the saved copy, and starts a download when it is missing or a day old.</summary>
    public static void Load(string folder, Action<string> log)
    {
        s_folder = folder;
        s_log = log;
        if ((s_facts != null && !s_force) || (s_loading != null && !s_loading.IsCompleted)) return;
        string file = Path.Combine(folder, "cleanup.json");
        if (s_facts == null && File.Exists(file)) s_facts = Parse(File.ReadAllText(file), log);
        bool stale = s_force || s_facts == null || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1);
        s_force = false;
        if (!stale) { State = "ready"; return; }
        s_lastTry = DateTime.UtcNow;
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
                    var got = Parse(json, log);
                    if (got == null || got.Count == 0) continue;
                    s_facts = got;
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(file, json);
                    State = "ready";
                    return;
                }
                catch { }
            }
            State = s_facts != null ? "ready (offline copy)" : "failed";
        });
    }

    static Dictionary<string, Fact>? Parse(string json, Action<string> log)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            var map = new Dictionary<string, Fact>();
            foreach (JsonProperty it in doc.RootElement.GetProperty("items").EnumerateObject())
            {
                JsonElement v = it.Value;
                var f = new Fact();
                if (v.TryGetProperty("n", out JsonElement n)) f.N = n.GetString() ?? "";
                if (v.TryGetProperty("p", out JsonElement p)) f.P = (long)p.GetDouble();
                if (v.TryGetProperty("g", out JsonElement g)) f.G = g.GetInt32();
                if (v.TryGetProperty("k", out JsonElement k)) f.K = k.GetInt32();
                f.U = v.TryGetProperty("u", out _);
                f.X = v.TryGetProperty("x", out _);
                f.B = v.TryGetProperty("b", out _);
                f.E = v.TryGetProperty("e", out _);
                if (v.TryGetProperty("c", out JsonElement c)) f.C = c.GetString() ?? "";
                if (v.TryGetProperty("s", out JsonElement s))
                    foreach (JsonProperty st in s.EnumerateObject()) f.S[st.Name] = st.Value.GetDouble();
                map[it.Name] = f;
            }
            return map;
        }
        catch (Exception e) { log("cleanup.json unreadable: " + e.Message); return null; }
    }

    static string Local(string id) => id.Contains(':') ? id[(id.IndexOf(':') + 1)..] : id;

    // b beats a: at least as good on every stat and rating, better on one (attack interval: lower is better).
    static bool Beats(Fact b, Fact a)
    {
        bool better = false;
        foreach (string k in a.S.Keys.Union(b.S.Keys))
        {
            double x = a.S.GetValueOrDefault(k), y = b.S.GetValueOrDefault(k);
            double good = k == "AttackInterval" ? (x == 0 ? 0 : x - y) : y - x;
            if (good < 0) return false;
            if (good > 0) better = true;
        }
        return better;
    }

    /// <summary>The bank items it suggests selling, spare first, then outgrown, then unused; most GP first in each.</summary>
    public static List<CleanRow> Rows(Game game, List<Row> bank)
    {
        var list = new List<CleanRow>();
        Dictionary<string, Fact>? F = s_facts;
        if (F == null) return list;
        // Gear you wear counts as "something better you own" too.
        var worn = new List<string>();
        try
        {
            CombatEntity? ce = game.Entities.GetAllObjects().OfType<CombatEntity>().FirstOrDefault();
            if (ce != null) worn.AddRange(ce.Equipment.EquippedItems.Select(i => Local(i.Id)));
        }
        catch { }
        var gear = bank.Select(r => Local(r.Item.Id)).Concat(worn).Distinct()
            .Where(id => F.TryGetValue(id, out Fact? f) && f.G == 1).ToList();

        foreach (Row r in bank)
        {
            string id = Local(r.Item.Id);
            if (r.Locked || !F.TryGetValue(id, out Fact? f)) continue;   // a locked stack is one you chose to keep
            if (f.G > 0)
            {
                if (f.U) continue;   // also an ingredient or upgrade material
                if (r.Qty > f.K) { list.Add(new CleanRow(r, "spare", f.K, $"you can only wear {(f.K == 1 ? "one" : "two")}")); continue; }
                if (f.G == 1 && !f.B && !f.E)
                {
                    string? better = gear.FirstOrDefault(b => b != id && F[b].C == f.C && !F[b].B && Beats(F[b], f));
                    if (better != null) list.Add(new CleanRow(r, "outgrown", 0, $"{F[better].N} is better on every stat"));
                }
                continue;
            }
            if (f.X) list.Add(new CleanRow(r, "unused", 0, "nothing in the game uses it"));
        }
        int Order(string g) => g == "spare" ? 0 : g == "outgrown" ? 1 : 2;
        return list.OrderBy(x => Order(x.Group)).ThenByDescending(x => x.Worth).ToList();
    }
}
