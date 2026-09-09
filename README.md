# CS2-Bot-Buy
CS2-Bot-Buy is a plugin based on CounterStrikeSharp that allows bots to buy everything and overhauls their economy logic.

## Features
1. Allow bots to purchase all weapons, including armor and defuse kits.

2. Remove the restriction preventing bots from buying in the first round of each half (including overtime).

3. Enable bots to drop weapons for teammates with less than $2800 when they have sufficient money.

4. Overhaul bots’ economy management strategy.

5. Team-wide economy classification (full buy / force / eco / save) instead of per-bot money checks.

6. Optional drop-chat lines when a bot gives a gun to a **human** teammate. Bot-to-bot drops stay silent. BotChat is optional: if the BotChat plugin is loaded, lines go through `botchat:api`; otherwise they fall back to `PrintToChatAll`.

## Installation
1. Download the latest BotBuy.zip from [Releases](https://github.com/ed0ard/CS2-Bot-Buy/releases)

2. Extract the folder and upload it to game/csgo/addons/counterstrikesharp/plugins on your server

3. Restart your server

(**Tip: Works better when `bot_eco_limit` is set to 2800**)
## Credits
Inspired by [droyer57](https://github.com/droyer57)
