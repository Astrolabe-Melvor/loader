namespace Astrolabe.Hold;

static class Help
{
    public const string Text = """
[b]Hold[/b] shows what your bank is worth and sells what you do not want to keep.

[b]Bank[/b]: what the whole bank and each tab sells for (with your sale bonuses). Click a tab button to show only that tab; search and filter the table.
[b]Rules[/b]: pick an item, then [i]Keep some, sell the rest[/i] or [i]Junk: always sell all[/i], and [i]Save rule[/i]. [i]Sell by my rules now[/i] sells what the rules allow (the line beside it says what first); [i]Sell by my rules automatically[/i] does it every 10 seconds. Locked bank stacks are never sold.
[b]Shop[/b]: every purchase the game shows you, with its cost and whether you can buy it (locked ones say what they need). Pick one to give it a rule:
  [i]Buy as soon as I can[/i] for upgrades with a buy limit (bank slots, stations, permits). [i]Rule: buy every upgrade listed[/i] adds it to every upgrade in the table, so filter by category first.
  Items with no buy limit (arena gear, arrows, buckets, charms) get [i]Buy once as soon as I can[/i] (only offered until you have bought it) or [i]Keep ... stocked[/i]: Hold buys until you have that many in the bank. Sell rules never sell below a stocked amount.
  Purchases that give no item can only be bought by hand. [i]Never spend below[/i] sets a reserve for each currency; item costs never take an item below its keep amount in Rules. Tick [i]Buy by my shop rules automatically[/i] to check every 10 seconds.
  [i]Buy order[/i]: [i]Cheapest first[/i] buys what takes the smallest share of your money first. [i]In my order[/i] numbers your rules (change it with [i]Earlier[/i] and [i]Later[/i]); one you can't afford yet makes later rules paid with the same currency or item wait, so it gets saved up for. With Beacon installed, you can get alerts when a rule buys something or a purchase with a rule unlocks.
[b]History[/b]: every sale and purchase Hold made, with today's and the all-time totals.
""";
}
