using System;
using System.Collections.Generic;
using Godot;
using MelvorModLoader;

namespace Mi2Kit;

/// <summary>Finds the game's UI fonts inside its pack (their folder can move between game versions).</summary>
static class Fonts
{
    public static void Find(out Font? body, out Font? head)
    {
        var files = new List<string>();
        try { Walk("res://", files, 0); }
        catch (Exception e) { Log.Write("Kit: font search failed: " + e.Message); }

        Log.Write("Kit: fonts in the game pack: " + string.Join(", ", files));
        // The game ships its UI face as "Default" (Assets/Fonts/Default/Static/Default_<Weight>.ttf).
        body = Load(files, "Default_Regular", "Default_Medium", "FiraSans-Regular", "DefaultVariableFont");
        head = Load(files, "Default_Bold", "Default_SemiBold", "Default_ExtraBold", "FiraSansCondensed-Bold", "Default_Black");
    }

    // Stylised variants (worn, outlined) are for logos; headings need the clean face.
    static readonly string[] s_styled = { "distress", "grunge", "worn", "rough", "outline", "stamp", "texture", "shadow" };

    static Font? Load(List<string> files, params string[] names)
    {
        // An exact file name ("FiraSans-Regular.ttf") wins over a prefix match.
        foreach (string want in names)
            foreach (string f in files)
            {
                string file = f.Substring(f.LastIndexOf('/') + 1);
                string stem = file.Contains('.') ? file[..file.IndexOf('.')] : file;
                if (!stem.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                Font? exact = ResourceLoader.Load<Font>(f);
                if (exact != null) { Log.Write("Kit font: " + f); return exact; }
            }
        foreach (string want in names)
            foreach (string f in files)
            {
                string file = f.Substring(f.LastIndexOf('/') + 1);
                if (!file.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
                bool styled = false;
                foreach (string w in s_styled) if (file.Contains(w, StringComparison.OrdinalIgnoreCase)) styled = true;
                if (styled) continue;
                if (want == "FiraSans" && file.StartsWith("FiraSansCondensed", StringComparison.OrdinalIgnoreCase)) continue;
                Font? font = ResourceLoader.Load<Font>(f);
                if (font != null) { Log.Write("Kit font: " + f); return font; }
            }
        return null;
    }

    static void Walk(string dir, List<string> found, int depth)
    {
        if (depth > 7) return;
        using DirAccess? d = DirAccess.Open(dir);
        if (d == null) return;
        string sep = dir.EndsWith('/') ? "" : "/";
        foreach (string f in d.GetFiles())
        {
            // Exported packs list "x.ttf.import" (or ".remap"); the resource loads by the plain name.
            string name = f.EndsWith(".import") ? f[..^7] : f.EndsWith(".remap") ? f[..^6] : f;
            if (name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".fontdata", StringComparison.OrdinalIgnoreCase))
                if (!found.Contains(dir + sep + name)) found.Add(dir + sep + name);
        }
        foreach (string sub in d.GetDirectories())
            if (!sub.StartsWith('.')) Walk(dir + sep + sub, found, depth + 1);
    }
}
