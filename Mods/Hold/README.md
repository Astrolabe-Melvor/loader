# Astrolabe Hold

What your bank is worth, what to keep, and what to buy. Built on Astrolabe Loader. **F9** opens it (rebindable in the F1 Mods menu).

## Bank

- Slots used and what the whole bank sells for, at your real sell prices (with sale bonuses).
- One button per bank tab with what that tab is worth; click one to show only that tab.
- A searchable table: amount, price each, total worth, tab and rule. Filters for items with or without a rule, and items worth nothing to sell.

## Rules

Pick an item, then:

- **Keep some, sell the rest:** keeps the amount you set and sells anything above it.
- **Junk: always sell all.**

**Sell by my rules now** sells everything the rules allow; the line beside it says what that would be first. **Sell by my rules automatically** does it every 10 seconds while you play. **Locked bank stacks are never sold**, rule or not. **Sell all now** sells one item outright.

## Cleanup

What in your bank is safe to sell, the same list as Astrolabe Tools' Bank cleanup:

- **Spare:** copies of gear beyond what you can wear (one, or two rings).
- **Outgrown:** gear another piece you own, in the bank or worn, beats on every stat.
- **Unused:** items nothing in the game needs.

Gear that is also an ingredient or upgrade material, gear with bonuses or in a set, and locked stacks are never listed. Tick items, then **Sell** them now or **Give ticked a rule** so your rules sell them from now on. What each item is for comes from Astrolabe Tools when it runs on this PC, else the wiki's `cleanup.json`, refreshed daily and after a game update.

## Shop

Every purchase the game shows you, with cost and status: owned, ready, can't afford, or locked (with what it needs, in the game's words). Filter by category, ready to buy, never bought, upgrades, items, purchases with a rule, or bought before.

- **Upgrades** (a buy limit, like bank slots, stations, permits): rule **Buy as soon as I can**. **Rule: buy every upgrade listed** gives that rule to every unowned upgrade in the current table.
- **Items** (no buy limit, give an item, like arena gear, arrows, buckets, charms): rule **Buy once as soon as I can** (offered until you have bought it once), or **Keep ... stocked** with an amount. Hold buys until you have at least that many in the bank, and sell rules never sell below it.
- Purchases that give no item can only be bought by hand.

**Never spend below** sets a reserve for each currency you hold (GP, slayer coins, marks). Item costs never take an item below its keep amount in Rules. **Buy order:** *Cheapest first* (the purchase taking the smallest share of your money goes first), or *In my order*, where rules are numbered (move them with **Earlier** and **Later**) and one you can't afford yet holds back later rules paid with the same currency or item, so it gets saved up for. With Beacon installed, alerts can fire when a rule buys something or a purchase with a rule unlocks.

**Buy by my shop rules automatically** checks every 10 seconds; a purchase that fails (no bank room, say) waits 5 minutes before the next try. Only purchases the game itself lets you buy are bought.

## History

Every sale and purchase made through Hold, with today's and all-time totals. Rules and the log are kept per character in `mods\Hold\rules`.
