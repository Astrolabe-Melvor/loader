using System;
using System.IO;
using System.Linq;
using GamesByMalcs.Melvor2;
using MelvorModLoader;

namespace Astrolabe.Course;

/// <summary>
/// Steps sent from outside the game: Astrolabe Tools drops a share code in mods\Course\inbox\*.crs ("Send to Course"
/// on the planner), and the queue adds its steps once a character is loaded. A line starting with # is a label.
/// </summary>
sealed class Inbox
{
    readonly string _folder;
    readonly Queue _queue;
    readonly Action<string> _log;
    double _next;

    public Inbox(string modFolder, Queue queue, Action<string> log)
    {
        _folder = Path.Combine(modFolder, "inbox");
        _queue = queue;
        _log = log;
    }

    public void Tick(Game? game, double now)
    {
        if (game == null || !_queue.HasCharacter || GameAccess.IsComputingOffline || now < _next) return;
        _next = now + 2;
        if (!Directory.Exists(_folder)) return;
        foreach (string file in Directory.EnumerateFiles(_folder, "*.crs").OrderBy(f => f))
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; }   // still being written; next time
            try { File.Delete(file); } catch { continue; }
            string label = lines.FirstOrDefault(l => l.StartsWith('#'))?.TrimStart('#').Trim() ?? "";
            string code = lines.FirstOrDefault(l => l.StartsWith("CRS1:") || l.StartsWith("AQ1:"))?.Trim() ?? "";
            var (steps, problems) = ShareCode.Import(game, code);
            if (steps.Count > 0) _queue.AddRange(steps);
            string from = label.Length > 0 ? $"\"{label}\"" : "Astrolabe Tools";
            string text = steps.Count > 0 ? $"Added {steps.Count} step{(steps.Count == 1 ? "" : "s")} from {from}" : $"Nothing added from {from}";
            if (problems.Count > 0) text += ": " + string.Join("; ", problems.Take(3));
            _queue.Raise(text);
            _log(text);
        }
    }
}
