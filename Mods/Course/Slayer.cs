using System;
using System.Collections.Generic;
using System.Linq;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Components.SlayerTasks;
using GamesByMalcs.Melvor2.Data.Currencies;
using GamesByMalcs.Melvor2.Entities.Combat;

namespace Astrolabe.Course;

/// <summary>One slayer master's task, as Course shows it.</summary>
public sealed record SlayerTask(
    ActiveSlayerMaster Master, string MasterId, string MasterName, Enemy? Enemy, string EnemyName, string AreaName,
    int Done, int Length, bool Extended, bool GameAuto, CurrencyQuantity Reroll, CurrencyQuantity Extend, ActionRef? Fight, bool Locked)
{
    public int Left => Math.Max(0, Length - Done);
}

/// <summary>
/// Slayer tasks: reads every master's task, extends and rerolls by your rules (never below your coin reserve), and
/// picks the task a "Slayer tasks" step fights next. The game's own Auto Slayer moves on to a master's next task when
/// you finish one while fighting there; a Slayer tasks step does the same for every master, in its own order.
/// </summary>
static class Slayer
{
    /// <summary>Target of a "Slayer tasks" step (its Entity is the combat skill).</summary>
    public const string Target = "*slayer";
    /// <summary>Counter key for finished tasks (the Times goal of a Slayer tasks step).</summary>
    public const string DoneKey = "*slayertasks";

    public static SlayerTasksComponent? Component(Game game) =>
        game.Entities.GetAllObjects().SelectMany(e => e.Components.Values).OfType<SlayerTasksComponent>().FirstOrDefault();

    public static long Have(Game game, Currency c) => ShopSource.Shop(game)?.Wallet.GetQuantity(c) ?? 0;

    static string CurrencyName(Currency c) => c.DisplayName.ToString();

    /// <summary>Unlocked masters, lowest first (the game's order).</summary>
    public static List<SlayerTask> Tasks(Game game)
    {
        var list = new List<SlayerTask>();
        SlayerTasksComponent? comp = Component(game);
        if (comp == null) return list;
        SkillChoice? combat = Catalog.Skills(game).FirstOrDefault(s => Gear.IsCombat(game, s.Id));
        foreach (var (data, m) in comp.ActiveMasters)
        {
            if (!(data.UnlockRequirements?.AreMet ?? true)) continue;
            Enemy? e = m.CurrentTask;
            ActionRef? fight = null;
            string areaName = "";
            bool locked = false;
            if (e != null && combat != null)
            {
                try
                {
                    CombatArea area = m.GetCurrentTaskArea(e);
                    areaName = area.DisplayName.ToString();
                    locked = !(area.UnlockRequirements?.AreMet ?? true);
                    fight = combat.Actions.FirstOrDefault(a => a.Ref.Target == e.Id && a.Ref.Area == area.Id)?.Ref
                        ?? new ActionRef { Entity = combat.Id, Target = e.Id, Area = area.Id, Label = $"{combat.Name}: {e.DisplayName} ({areaName})" };
                }
                catch { }
            }
            CurrencyQuantity reroll = data.RerollCost, extend = data.RerollCost;
            try { reroll = m.Stats.RerollCost.Value; extend = m.Stats.ExtensionCost.Value; } catch { }
            list.Add(new SlayerTask(m, data.Id, data.DisplayName.ToString(), e, e?.DisplayName.ToString() ?? "", areaName,
                m.TaskProgress, m.TaskLength, m.IsTaskExtended, m.AutoSlayerEnabled, reroll, extend, fight, locked));
        }
        return list;
    }

    public static string CostText(CurrencyQuantity c) => $"{c.Quantity:N0} {CurrencyName(c.Currency)}";

    /// <summary>"Galan: Harpy 24 / 175" for each task, for notes.</summary>
    public static string Summary(Game game)
    {
        var t = Tasks(game).Where(x => x.Enemy != null).ToList();
        return t.Count == 0 ? "No slayer tasks right now." : "Your tasks: " + string.Join("; ", t.Select(x => $"{x.MasterName}: {x.EnemyName} {x.Done:N0} / {x.Length:N0}{(x.Locked ? " (area locked)" : "")}")) + ".";
    }

    /// <summary>The task (if any) a monster is for, as "Slayer task from Galan: 24 / 175".</summary>
    public static string TaskNote(Game game, IEnumerable<Enemy> enemies)
    {
        var set = enemies.ToHashSet();
        var hit = Tasks(game).Where(t => t.Enemy != null && set.Contains(t.Enemy)).ToList();
        return hit.Count == 0 ? "" : string.Join(" ", hit.Select(t => $"Slayer task from {t.MasterName}: {t.Done:N0} / {t.Length:N0}."));
    }


    public static bool CanPay(Game game, QueueState state, CurrencyQuantity cost) =>
        Have(game, cost.Currency) - cost.Quantity >= (cost.Currency.Id == CurrencyIds.SlayerCoins ? state.SlayerReserve : 0);

    public static string Reroll(Game game, SlayerTask t)
    {
        if (t.Enemy == null) return "no task to reroll";
        if (Have(game, t.Reroll.Currency) < t.Reroll.Quantity) return $"needs {CostText(t.Reroll)}";
        try { t.Master.RerollTask(); } catch (Exception e) { return e.Message; }
        return "";
    }

    public static string Extend(Game game, SlayerTask t)
    {
        if (t.Enemy == null) return "no task to extend";
        if (t.Extended) return "already extended";
        if (Have(game, t.Extend.Currency) < t.Extend.Quantity) return $"needs {CostText(t.Extend)}";
        try { t.Master.ExtendTask(); } catch (Exception e) { return e.Message; }
        return "";
    }

    /// <summary>Extends and rerolls by the rules, keeping the coin reserve. Returns what it did, for the log.</summary>
    public static string ApplyRules(Game game, QueueState state)
    {
        var said = new List<string>();
        foreach (SlayerTask t0 in Tasks(game))
        {
            SlayerTask t = t0;
            // Reroll monsters you skip (and, if asked, ones whose area you can't enter). A few tries at most per check.
            for (int i = 0; i < 3 && t.Enemy != null && (state.SlayerSkip.Contains(t.Enemy.Id) || (state.SlayerRerollLocked && t.Locked)); i++)
            {
                if (!CanPay(game, state, t.Reroll)) break;
                string was = t.EnemyName;
                if (Reroll(game, t).Length > 0) break;
                t = Tasks(game).First(x => x.MasterId == t.MasterId);
                said.Add($"rerolled {was} from {t.MasterName} ({CostText(t0.Reroll)}), now {t.EnemyName}");
            }
            if (t.Enemy != null && !t.Extended && state.SlayerExtend.Contains(t.MasterId) && !state.SlayerSkip.Contains(t.Enemy.Id) && CanPay(game, state, t.Extend))
                if (Extend(game, t).Length == 0) said.Add($"extended {t.EnemyName} from {t.MasterName} ({CostText(t.Extend)})");
        }
        return string.Join("; ", said);
    }

    /// <summary>The task to fight now: highest master first, skipping locked areas, monsters you skip and ones that
    /// just failed to start.</summary>
    public static ActionRef? Pick(Game game, QueueState state, Dictionary<string, double> failedUntil, double now) =>
        Tasks(game).AsEnumerable().Reverse()
            .Where(t => t.Fight != null && !t.Locked && t.Enemy != null && !state.SlayerSkip.Contains(t.Enemy.Id))
            .Select(t => t.Fight!)
            .FirstOrDefault(f => !failedUntil.TryGetValue(f.Target, out double until) || now >= until);
}
