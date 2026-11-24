# Astrolabe Spyglass

Look up any item without leaving the game. Built on Astrolabe Loader. **F10** opens it (rebindable in the F1 Mods menu).

- **Search** every item by name.
- **How to get it:** recipes, monster and dungeon drops (with amounts and chances), chests, shops and quest rewards.
- **Used for:** everything that takes it as an ingredient or cost, and how many.
- **Needed to unlock or do:** actions and purchases that require it.
- How many you have and what it sells for.
- **Plan it in Course:** opens Course's planner on the item with the amount you set (needs Astrolabe Course).
- **Open in the wiki** for the full page. Double-click a line to look that item up, or to open it in the wiki; **Back** returns.

The item data comes from Astrolabe Tools when it runs on this PC, otherwise from the wiki's `spyglass.json`. A copy is kept in `mods\Spyglass` for offline use and refreshed once a day.

Other mods can open an item in Spyglass with `Messages.Publish("spyglass.show", "item id")`.
