using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch : BasePlugin
{
    public override string ModuleName => "BotBuyPatch";
    public override string ModuleVersion => "2.0.5";
    public override string ModuleAuthor => "ed0ard";
    public override string ModuleDescription => "Team-economy buyer: pistol/eco/force/anti-eco/full-buy";

    private readonly Dictionary<int, List<string>> _prevWeapons = new();
    private readonly Dictionary<int, int> _prevMoney = new();
    private readonly Dictionary<int, int> _prevArmor = new();
    private readonly Dictionary<int, int> _startMoney = new();

    private readonly Dictionary<CsTeam, int> _lossIndex = new()
    {
        [CsTeam.Terrorist] = 0,
        [CsTeam.CounterTerrorist] = 0,
    };

    private CsTeam _lastWinner = CsTeam.None;
    private bool _pistolThisRound;
    private int _buyEpoch;
    private Dictionary<CsTeam, BuyRound> _roundTypeByTeam = new();

    private readonly Dictionary<CsTeam, TeamTemperament> _temperament = new()
    {
        [CsTeam.Terrorist] = TeamTemperament.Balanced,
        [CsTeam.CounterTerrorist] = TeamTemperament.Balanced,
    };

    private readonly Dictionary<int, int> _tierByUserId = new();

    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
            RemoveBot(player);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid)
            ClearPreviousInventory(player);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SavePreviousInventory();
        UpdateLossBonus((CsTeam)@event.Winner);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        SnapshotStartMoney();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        int epoch = ++_buyEpoch;
        SnapshotStartMoney();

        if (IsFirstRoundOfHalf())
        {
            _lossIndex[CsTeam.Terrorist] = 0;
            _lossIndex[CsTeam.CounterTerrorist] = 0;
            _lastWinner = CsTeam.None;
            _temperament[CsTeam.Terrorist] = BotStrategy.RollTemperament(Random.Shared);
            _temperament[CsTeam.CounterTerrorist] = BotStrategy.RollTemperament(Random.Shared);
        }

        var startValues = _startMoney.Values.ToList();
        int startMedian = BotStrategy.Median(startValues);
        _pistolThisRound = IsFirstRoundOfHalf() && startMedian > 0 && startMedian <= 1200;

        if (Server.MapName == "aim_rush" || Server.MapName == "rush_001")
            return HookResult.Continue;

        ConVar<string>? botLoadout = ConVar<string>.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.Value))
            return HookResult.Continue;

        if (IsWarmupPeriod() || IsEconomyDisabled())
            return HookResult.Continue;

        AddTimer(0.55f, () =>
        {
            if (epoch != _buyEpoch)
                return;
            ApplyEconomyBuys();
        });

        AddTimer(2.0f, () =>
        {
            if (epoch != _buyEpoch)
                return;
            DropWeaponsToPoor();
        });

        AddTimer(2.5f, () =>
        {
            if (epoch != _buyEpoch)
                return;
            GiftArmor();
        });

        return HookResult.Continue;
    }

    private void ApplyEconomyBuys()
    {
        var players = CollectPlayers();
        if (players.Count == 0)
            return;

        var t = players.Where(p => p.Team == CsTeam.Terrorist).ToList();
        var ct = players.Where(p => p.Team == CsTeam.CounterTerrorist).ToList();

        int tMedian = t.Count == 0 ? 0 : BotStrategy.Median(t.Select(p => p.StartMoney).ToList());
        int ctMedian = ct.Count == 0 ? 0 : BotStrategy.Median(ct.Select(p => p.StartMoney).ToList());
        bool firstOfHalf = IsFirstRoundOfHalf();
        _pistolThisRound = firstOfHalf && Math.Max(tMedian, ctMedian) <= 1200
            && Math.Max(tMedian, ctMedian) > 0;

        var tInputs = t.Select(s => s.ToBotInput()).ToList();
        var ctInputs = ct.Select(s => s.ToBotInput()).ToList();

        var tPlan = BotStrategy.ClassifyTeam(tInputs, ctInputs, MakeContext(CsTeam.Terrorist, firstOfHalf), Random.Shared);
        var ctPlan = BotStrategy.ClassifyTeam(ctInputs, tInputs, MakeContext(CsTeam.CounterTerrorist, firstOfHalf), Random.Shared);
        _roundTypeByTeam[CsTeam.Terrorist] = tPlan.Round;
        _roundTypeByTeam[CsTeam.CounterTerrorist] = ctPlan.Round;

        Server.PrintToConsole(
            $"[BotBuy] T={tPlan.Round}({tPlan.Temperament}) med={tPlan.Median} CT={ctPlan.Round}({ctPlan.Temperament}) med={ctPlan.Median}");

        ApplyTeam(t, tPlan, ctPlan);
        ApplyTeam(ct, ctPlan, tPlan);
    }

    private TeamContext MakeContext(CsTeam team, bool firstOfHalf)
    {
        return new TeamContext
        {
            FirstRoundOfHalf = firstOfHalf,
            PistolRound = _pistolThisRound,
            LastWinner = _lastWinner,
            LossIndex = _lossIndex.GetValueOrDefault(team),
            Temperament = _temperament.GetValueOrDefault(team),
        };
    }

    private void ApplyTeam(List<PlayerSnap> team, TeamBuyPlan plan, TeamBuyPlan enemyPlan)
    {
        int lossIndex = team.Count == 0 ? 0 : _lossIndex.GetValueOrDefault(team[0].Team);
        foreach (var snap in team
                     .Where(s => s.Player.IsBot)
                     .OrderBy(s => s.Personality == BotPersonality.Sniper ? 0 : 1))
        {
            if (!snap.Player.IsValid)
                continue;
            var intended = BotStrategy.PickLoadout(snap.ToBotInput(), plan, enemyPlan, lossIndex, Random.Shared);
            ApplyLoadout(snap.Player, intended, plan.Round);
        }
    }

    private List<PlayerSnap> CollectPlayers()
    {
        _tierByUserId.Clear();
        var list = new List<PlayerSnap>();
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!IsLiveCombatant(player))
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            int startMoney = EstimateStartMoney(player, pawn);
            var (prevWeapons, _, _) = PreviousInventory(player);
            string? valveGun = WeaponCatalog.FindPrimary(pawn);
            int tier = player.IsBot ? BotStrategy.MapSkillTier(player.PawnBotDifficulty) : 1;
            int userId = UserIdOf(player);
            if (userId != -1)
                _tierByUserId[userId] = tier;

            list.Add(new PlayerSnap
            {
                Player = player,
                Team = player.Team,
                StartMoney = startMoney,
                LeftoverRifle = prevWeapons.Any(WeaponCatalog.IsRifle) || prevWeapons.Any(WeaponCatalog.IsAwp),
                CurrentPrimary = valveGun,
                Personality = BotStrategy.PersonalityFromGun(valveGun),
                SkillTier = tier,
            });
        }
        return list;
    }

    private int EstimateStartMoney(CCSPlayerController player, CCSPlayerPawn pawn)
    {
        int current = player.InGameMoneyServices?.Account ?? 0;
        var (prevWeapons, _, prevArmor) = PreviousInventory(player);
        int reconstructed = current;

        foreach (var weapon in WeaponCatalog.Weapons(pawn))
        {
            if (prevWeapons.Contains(weapon))
                continue;
            int price = WeaponCatalog.PriceOf(weapon);
            if (price > 0)
                reconstructed += price;
        }

        if (pawn.ArmorValue > 0 && prevArmor <= 0)
            reconstructed += WeaponCatalog.HasHelmet(pawn) ? 1000 : 650;

        int userId = player.UserId ?? -1;
        if (userId != -1 && _startMoney.TryGetValue(userId, out int snap) && snap > reconstructed)
            return snap;
        return Math.Max(current, reconstructed);
    }

    private void SnapshotStartMoney()
    {
        _startMoney.Clear();
        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!IsLiveCombatant(player))
                continue;
            int userId = player.UserId ?? -1;
            if (userId == -1)
                continue;
            _startMoney[userId] = player.InGameMoneyServices?.Account ?? 0;
        }
    }

    private void DropWeaponsToPoor()
    {
        if (_pistolThisRound)
            return;

        var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(IsLiveCombatant)
            .ToList();

        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            var round = _roundTypeByTeam.GetValueOrDefault(team);
            if (round is not (BuyRound.FullBuy or BuyRound.Force))
                continue;

            var poor = players
                .Where(p => p.Team == team && p.IsValid && !HasPrimaryWeapon(p))
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
            var richBots = players
                .Where(p => p.Team == team && p.IsBot && p.IsValid && p.InGameMoneyServices?.Account >= 2700)
                .ToList();

            if (poor.Count == 0 || richBots.Count == 0)
                continue;

            var gifted = new HashSet<CCSPlayerController>();
            int poorIndex = 0;
            bool awpDropped = TeamHasAwp(players, team);
            foreach (var rich in richBots)
            {
                if (poorIndex >= poor.Count)
                    break;
                if (!IsLiveCombatant(rich) || rich.InGameMoneyServices == null)
                    continue;

                bool Eligible(CCSPlayerController p) =>
                    IsLiveCombatant(p) && !gifted.Contains(p) && !HasPrimaryWeapon(p);

                int given = 0;
                while (given < 3 && poorIndex < poor.Count)
                {
                    bool allowAwp = !awpDropped && HasPrimaryWeapon(rich);
                    string? gun = BotStrategy.PickDroppedPrimary(team, rich.InGameMoneyServices.Account, allowAwp, Random.Shared);
                    if (gun == null)
                        break;
                    int price = WeaponCatalog.PriceOf(gun);
                    if (price <= 0 || rich.InGameMoneyServices.Account < price)
                        break;

                    CCSPlayerController receiver;
                    if (WeaponCatalog.IsAwp(gun))
                    {
                        // The team awp goes to the highest-skill eligible receiver in line.
                        var candidate = poor.Skip(poorIndex).Where(Eligible).OrderByDescending(TierOf).FirstOrDefault();
                        if (candidate == null)
                            break;
                        poor.Remove(candidate);
                        receiver = candidate;
                    }
                    else
                    {
                        receiver = poor[poorIndex++];
                    }

                    if (!Eligible(receiver))
                        continue;

                    var weapon = receiver.GiveNamedItem<CCSWeaponBase>(gun);
                    if (weapon == null || !weapon.IsValid)
                        continue;
                    WeaponCatalog.SetWeaponOriginalOwner(weapon, rich);
                    gifted.Add(receiver);

                    rich.InGameMoneyServices.Account -= price;
                    if (rich.InGameMoneyServices.Account < 0)
                        rich.InGameMoneyServices.Account = 0;
                    Utilities.SetStateChanged(rich, "CCSPlayerController", "m_pInGameMoneyServices");

                    if (WeaponCatalog.IsAwp(gun))
                        awpDropped = true;

                    AnnounceDrop(rich, receiver, given);
                    given++;
                }
            }
        }
    }

    private int TierOf(CCSPlayerController player)
    {
        int userId = UserIdOf(player);
        return userId != -1 && _tierByUserId.TryGetValue(userId, out int tier) ? tier : 1;
    }

    private static bool TeamHasAwp(List<CCSPlayerController> players, CsTeam team)
    {
        return players.Any(player =>
        {
            if (!player.IsValid || player.Team != team)
                return false;
            var pawn = player.PlayerPawn.Value;
            return pawn != null && pawn.IsValid && WeaponCatalog.IsAwp(WeaponCatalog.FindPrimary(pawn));
        });
    }

    private void GiftArmor()
    {
        if (_pistolThisRound)
            return;

        var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p => IsLiveCombatant(p) && p.IsBot)
            .ToList();

        foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
        {
            var round = _roundTypeByTeam.GetValueOrDefault(team);
            if (round is BuyRound.Eco or BuyRound.Pistol)
                continue;

            while (true)
            {
                var needArmor = players
                    .Where(p => IsLiveCombatant(p) && p.Team == team && HasPrimaryWeapon(p)
                        && (p.PlayerPawn.Value?.ArmorValue ?? 1) == 0)
                    .ToList();
                if (needArmor.Count == 0)
                    break;

                var buyer = players
                    .Where(p => IsLiveCombatant(p) && p.Team == team && p.InGameMoneyServices?.Account >= 650)
                    .OrderByDescending(p => p.InGameMoneyServices!.Account)
                    .FirstOrDefault();
                if (buyer?.InGameMoneyServices == null)
                    break;

                var target = needArmor[Random.Shared.Next(needArmor.Count)];
                if (!IsLiveCombatant(target))
                    continue;

                int buyerMoney = buyer.InGameMoneyServices.Account;
                if (team == CsTeam.Terrorist && buyerMoney < 1000)
                    break;

                string item = buyerMoney >= 1000 ? "item_assaultsuit" : "item_kevlar";
                int price = buyerMoney >= 1000 ? 1000 : 650;
                target.GiveNamedItem(item);
                buyer.InGameMoneyServices.Account -= price;
                Utilities.SetStateChanged(buyer, "CCSPlayerController", "m_pInGameMoneyServices");
            }
        }
    }

    private static CCSGameRules? GetGameRules()
    {
        try
        {
            return Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWarmupPeriod()
    {
        try
        {
            return GetGameRules()?.WarmupPeriod == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEconomyDisabled()
    {
        return (ConVar<int>.Find("mp_maxmoney")?.Value ?? 16000) <= 0;
    }

    //----------------------------------------------------------------------------------------------
    // Marks natively-purchased bot weapons with their original owner so drops show attribution.
    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot ||
            player.SteamID == 0 || CoreConfig.FollowCS2ServerGuidelines)
            return HookResult.Continue;

        var weapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
        if (weapons == null) return HookResult.Continue;

        string weaponName = @event.Weapon.StartsWith("weapon_")
            ? @event.Weapon : "weapon_" + @event.Weapon;
        foreach (var handle in weapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid || weapon.DesignerName != weaponName) continue;

            var csWeapon = weapon.As<CCSWeaponBase>();
            // Do not claim a previously owned weapon when handling native purchases.
            if (csWeapon.PrevOwner.IsValid || csWeapon.OriginalOwnerXuidLow != 0 ||
                csWeapon.OriginalOwnerXuidHigh != 0) continue;

            WeaponCatalog.SetWeaponOriginalOwner(csWeapon, player);
        }
        return HookResult.Continue;
    }

    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = GetGameRules();
            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar<int>.Find("mp_maxrounds")?.Value ?? 24;
            int otMaxRounds = ConVar<int>.Find("mp_overtime_maxrounds")?.Value ?? 6;
            if (maxRounds <= 0) maxRounds = 24;
            if (otMaxRounds <= 0) otMaxRounds = 6;

            int half = maxRounds / 2;
            int otHalf = otMaxRounds / 2;

            return played == 0
                || played == half
                || played == maxRounds
                || (played > maxRounds && (played - maxRounds) % otHalf == 0);
        }
        catch
        {
            return false;
        }
    }

    private static int UserIdOf(CCSPlayerController player)
    {
        return player.IsValid ? player.UserId ?? -1 : -1;
    }

    private static bool IsLiveCombatant(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsHLTV)
            return false;
        if (player.Connected != PlayerConnectedState.Connected)
            return false;
        if (player.Team != CsTeam.Terrorist && player.Team != CsTeam.CounterTerrorist)
            return false;
        var pawn = player.PlayerPawn.Value;
        return pawn != null && pawn.IsValid;
    }

    private static bool IsHumanDropReceiver(CCSPlayerController? player)
    {
        return IsLiveCombatant(player) && !player!.IsBot;
    }

    private void RemoveBot(CCSPlayerController player)
    {
        int userId = UserIdOf(player);
        if (userId == -1)
            return;
        _prevWeapons.Remove(userId);
        _prevMoney.Remove(userId);
        _prevArmor.Remove(userId);
        _startMoney.Remove(userId);
    }

    private void SavePreviousInventory()
    {
        if (IsFirstRoundOfHalf())
        {
            _prevWeapons.Clear();
            _prevMoney.Clear();
            _prevArmor.Clear();
            return;
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid)
                continue;
            int userId = UserIdOf(player);
            if (userId == -1)
                continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            _prevWeapons[userId] = WeaponCatalog.Weapons(pawn).ToList();
            _prevMoney[userId] = player.InGameMoneyServices?.Account ?? 0;
            _prevArmor[userId] = pawn.ArmorValue;
        }
    }

    private void ClearPreviousInventory(CCSPlayerController player)
    {
        int userId = UserIdOf(player);
        if (userId == -1)
            return;
        _prevWeapons.Remove(userId);
        _prevArmor.Remove(userId);
    }

    private (List<string> Weapons, int Money, int Armor) PreviousInventory(CCSPlayerController player)
    {
        var weapons = new List<string>();
        int money = 0;
        int armor = 0;
        int userId = UserIdOf(player);
        if (userId == -1)
            return (weapons, money, armor);
        if (IsFirstRoundOfHalf())
            return (weapons, money, armor);
        if (_prevWeapons.TryGetValue(userId, out var prev))
            weapons = prev;
        if (_prevMoney.TryGetValue(userId, out int m))
            money = m;
        if (_prevArmor.TryGetValue(userId, out int a))
            armor = a;
        return (weapons, money, armor);
    }

    [ConsoleCommand("css_botbuy", "BotBuy debug commands")]
    public void OnBotBuyCommand(CCSPlayerController? caller, CommandInfo command)
    {
        if (command.GetArg(1) == "diffcheck")
        {
            DiffCheck(command);
            return;
        }
        command.ReplyToCommand("[BotBuy] usage: css_botbuy diffcheck");
    }

    private void DiffCheck(CommandInfo command)
    {
        command.ReplyToCommand(
            $"[BotBuy] plans T={_roundTypeByTeam.GetValueOrDefault(CsTeam.Terrorist)}({_temperament[CsTeam.Terrorist]})" +
            $" CT={_roundTypeByTeam.GetValueOrDefault(CsTeam.CounterTerrorist)}({_temperament[CsTeam.CounterTerrorist]})" +
            $" lossT={_lossIndex[CsTeam.Terrorist]} lossCT={_lossIndex[CsTeam.CounterTerrorist]}");

        List<CCSPlayerController> bots;
        try
        {
            bots = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                .Where(p => p.IsValid && p.IsBot && p.Connected == PlayerConnectedState.Connected)
                .OrderBy(p => (int)p.Team)
                .ToList();
        }
        catch (Exception)
        {
            command.ReplyToCommand("[BotBuy] entity system not ready, retry after map load");
            return;
        }

        if (bots.Count == 0)
        {
            command.ReplyToCommand("[BotBuy] no bots connected");
            return;
        }

        foreach (var p in bots)
        {
            int raw = p.PawnBotDifficulty;
            int tier = BotStrategy.MapSkillTier(raw);
            command.ReplyToCommand(
                $"[BotBuy] {p.PlayerName} team={p.Team} rawDifficulty={raw} tier={tier}" +
                $" money={p.InGameMoneyServices?.Account ?? -1}");
        }
    }
}
