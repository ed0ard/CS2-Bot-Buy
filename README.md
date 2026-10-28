# CS2-Bot-Buy
CS2-Bot-Buy is a plugin based on CounterStrikeSharp that allows bots to buy everything and overhauls their economy logic.

## Features
1. Allow bots to purchase all weapons, including armor and defuse kits.

2. Remove the restriction preventing bots from buying in the first round of each half (including overtime).

3. Help unarmed teammates when the donor can afford a rifle without ending up with less cash than the recipient. Drops keep the donor's weapon skin and original owner.

4. Overhaul bots’ economy management strategy.

## Installation
1. Download the latest BotBuy.zip from [Releases](https://github.com/ed0ard/CS2-Bot-Buy/releases)

2. Extract the folder and upload it to game/csgo/addons/counterstrikesharp/plugins on your server

3. Restart your server

(**Tip: Works better when `bot_eco_limit` is set to 2800**)

## Native purchases
The plugin adjusts native purchase requests before they spend money. The existing pistol, force-buy, weapon replacement and overtime choices are retained; CS2 handles purchase eligibility, prices, inventory and purchase events. There is no delayed buy/refund/swap cycle. The free starting USP-S choice remains a spawn loadout operation.

Install `native-purchases.json` beside `BotBuy.dll`. The same managed DLL supports Windows x64 and Linux x64, targeting CS2 1.41.9.2 / SourceRevision 11162119. The config selects the host platform's six purchase-related entries, expected bytes and server fingerprint; an unknown binary is rejected before hooks are installed. Both profiles must be audited again after a game update; changing only the hash is not sufficient.

The Linux bindings were checked against the matching Steam depot `libserver.so`, including the System V argument registers, inventory wrapper and default-item/price call chain. All six entries on each platform match uniquely. This is static ABI validation, not a Linux server playtest; purchase events, donations and plugin reload still need an in-game check on Linux before a stable release.

Missing bot loadout weapons use unused slots backed by CS2's default item views. Equipped items retain their existing views. Weapon donations use the donor's native buy-and-drop path, so an installed cosmetic plugin can supply that bot's skin. BotBuy does not depend on a cosmetics or replay plugin.

Assistance runs at freeze end and on subsequent purchase/buy-zone events. Armor sponsorship funds the recipient's native armor purchase and charges the sponsor; armor cannot be physically dropped. Actual native prices replace the old manual refund credits, and armor above 40 is preserved by rejecting a refill before purchase.

Build with `dotnet build -c Release`. Package `BotBuy.dll`, `BotBuy.deps.json` and the matching `native-purchases.json` together for either supported platform.
## Credits
Inspired by [droyer57](https://github.com/droyer57)
