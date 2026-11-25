using System;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using Astrolabe.UI;

namespace MelvorModLoader;

/// <summary>
/// Mods menu > Report a bug. Puts together what a bug report needs (versions, which mods loaded, errors, the end of
/// the log) with the player's own words, then copies it, saves it, or opens a GitHub issue to paste it into.
/// Nothing is sent anywhere by itself.
/// </summary>
static class BugReport
{
    const int LogLines = 300;
    static KitWindow? s_win;
    static OptionButton s_mod = null!;
    static TextEdit s_what = null!;
    static CheckBox s_log = null!;
    static Label s_note = null!;

    public static void Open(string mod = "")
    {
        if (s_win == null) Build();
        s_mod.Clear();
        s_mod.AddItem("Not sure / the loader");
        foreach (ModInfo m in Loader.Mods.OrderBy(m => m.Name)) s_mod.AddItem(m.Name);
        int pick = Enumerable.Range(0, s_mod.ItemCount).FirstOrDefault(i => s_mod.GetItemText(i) == mod);
        // Default to a mod that hit errors this session, if there is one.
        if (pick == 0 && Loader.Mods.FirstOrDefault(m => m.Errors > 0 || m.Failed.Length > 0) is { } bad)
            pick = Enumerable.Range(0, s_mod.ItemCount).FirstOrDefault(i => s_mod.GetItemText(i) == bad.Name);
        s_mod.Select(pick);
        s_note.Text = "";
        s_win!.Open();
    }

    static void Build()
    {
        s_win = new KitWindow("Report a bug", "nothing is sent until you paste it", new Vector2(620, 0), 72);
        s_win.Body.AddChild(Kit.Row(Kit.Label("Which mod:", null, 0, "Dim"), s_mod = new OptionButton { CustomMinimumSize = new Vector2(220, 0) }));

        s_win.Body.AddChild(Kit.Label("What happened, and what you expected instead. Steps to make it happen again help most.", null, 13, "Dim"));
        s_what = new TextEdit
        {
            CustomMinimumSize = new Vector2(580, 160),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            PlaceholderText = "Example: I queued Jump 3: Advanced Path in Course, but it ran the Simple Path.",
        };
        s_win.Body.AddChild(s_what);

        s_log = new CheckBox { Text = $"Include the end of the log ({LogLines} lines). Your Windows user name is taken out.", ButtonPressed = true };
        s_win.Body.AddChild(s_log);

        Button issue = Kit.Btn("Copy and open a GitHub issue", OpenIssue, action: true);
        issue.TooltipText = "Copies the report, then opens a new issue on GitHub. Paste it into the description (Ctrl+V). Needs a free GitHub account.";
        Button copy = Kit.Btn("Copy report", () => { DisplayServer.ClipboardSet(Build(true)); s_note.Text = "Copied. Paste it wherever you're reporting (Discord, GitHub)."; });
        Button save = Kit.Btn("Save to file", Save);
        save.TooltipText = "Writes it to mods\\bug-reports\\ and opens that folder.";
        Button preview = Kit.Btn("Preview", () => { s_note.Text = Build(false).Length + " characters. Copy it to see everything."; });
        s_win.Body.AddChild(Kit.Row(issue, copy, save, Kit.Spacer(), preview));

        s_note = Kit.Label("", Kit.Good, 13);
        s_note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        s_note.CustomMinimumSize = new Vector2(580, 0);
        s_win.Body.AddChild(s_note);
        s_win.CloseText = "Close";
    }

    static string Mod => s_mod.Selected <= 0 ? "Loader" : s_mod.GetItemText(s_mod.Selected);

    static string Build(bool withLog)
    {
        var b = new StringBuilder();
        string what = s_what.Text.Trim();
        b.AppendLine("### What happened").AppendLine(what.Length > 0 ? what : "(not filled in)").AppendLine();
        b.AppendLine("### Versions");
        b.AppendLine($"- Mod: {Mod}");
        b.AppendLine($"- {Loader.Name} {Loader.Version}");
        b.AppendLine($"- Game {GameWatch.Version}{(GameWatch.UpdatedFrom.Length > 0 ? $" (updated from {GameWatch.UpdatedFrom} this session)" : "")}");
        b.AppendLine($"- {OS.GetName()} {OS.GetVersion()}, .NET {System.Environment.Version}");
        b.AppendLine().AppendLine("### Mods");
        foreach (ModInfo m in Loader.Mods.OrderBy(m => m.Name))
        {
            string state = m.Failed.Length > 0 ? "did not load: " + m.Failed
                : m.Stopped ? $"paused after errors ({m.Errors}): {m.LastError}"
                : m.Errors > 0 ? $"{m.Errors} error(s), last: {m.LastError}"
                : "ok";
            b.AppendLine($"- {m.Name} {m.Version}: {state}");
            if (m.Missing.Count > 0) b.AppendLine($"  - missing in this game version: {string.Join("; ", m.Missing.Take(12))}");
        }
        if (withLog && s_log.ButtonPressed)
        {
            b.AppendLine().AppendLine("### Log (end)").AppendLine("```");
            try
            {
                string[] lines = File.ReadAllLines(Log.FilePath);
                foreach (string l in lines.Skip(Math.Max(0, lines.Length - LogLines))) b.AppendLine(Private(l));
            }
            catch (Exception e) { b.AppendLine("(could not read the log: " + e.Message + ")"); }
            b.AppendLine("```");
        }
        return b.ToString();
    }

    // C:\Users\Ian\... -> %USERPROFILE%\...
    static string Private(string line)
    {
        string home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        if (home.Length > 3) line = line.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = System.Environment.UserName;
        if (user.Length > 2) line = line.Replace("\\" + user + "\\", "\\%USERNAME%\\", StringComparison.OrdinalIgnoreCase);
        return line;
    }

    static void OpenIssue()
    {
        DisplayServer.ClipboardSet(Build(true));
        string first = s_what.Text.Trim().Split('\n')[0];
        string title = $"[{Mod}] " + (first.Length > 0 ? (first.Length > 70 ? first[..67] + "..." : first) : "Bug report");
        // The report goes on the clipboard: logs are too long for a link.
        string body = "Paste the report from Astrolabe here (Ctrl+V). Screenshots help too.\n\n";
        OS.ShellOpen($"https://github.com/{Updates.Repo}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}");
        s_note.Text = "Report copied. In the GitHub page that opened, click the description box and press Ctrl+V.";
    }

    static void Save()
    {
        try
        {
            string dir = Path.Combine(Startup.ModsFolder, "bug-reports");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{Mod}.md");
            File.WriteAllText(file, Build(true));
            OS.ShellOpen(dir);
            s_note.Text = "Saved " + Path.GetFileName(file) + ".";
        }
        catch (Exception e) { s_note.Text = "Could not save: " + e.Message; }
    }
}
