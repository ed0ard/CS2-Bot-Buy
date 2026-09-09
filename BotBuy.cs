using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
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

    private readonly Dictionary<int, int> _botUserIdToIndex = new();
    private int _botIndexCounter;

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

    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsBot)
            GetBotIndex(player);
        return HookResult.Continue;
    }

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
        if (player != null && player.IsValid && player.IsBot)
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
        }

        var startValues = _startMoney.Values.ToList();
        int startMedian = Median(startValues);
        _pistolThisRound = IsFirstRoundOfHalf() && startMedian > 0 && startMedian <= 1200;

        if (Server.MapName == "aim_rush")
            return HookResult.Continue;

        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
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

        int tMedian = t.Count == 0 ? 0 : Median(t.Select(p => p.StartMoney).ToList());
        int ctMedian = ct.Count == 0 ? 0 : Median(ct.Select(p => p.StartMoney).ToList());
        _pistolThisRound = IsFirstRoundOfHalf() && Math.Max(tMedian, ctMedian) <= 1200
            && Math.Max(tMedian, ctMedian) > 0;

        var tPlan = ClassifyTeam(t, ct);
        var ctPlan = ClassifyTeam(ct, t);
        _roundTypeByTeam[CsTeam.Terrorist] = tPlan.Round;
        _roundTypeByTeam[CsTeam.CounterTerrorist] = ctPlan.Round;

        Server.PrintToConsole(
            $"[BotBuy] T={tPlan.Round} med={tPlan.Median} CT={ctPlan.Round} med={ctPlan.Median}");

        ApplyTeam(t, tPlan);
        ApplyTeam(ct, ctPlan);
    }

    private void ApplyTeam(List<PlayerSnap> team, TeamBuyPlan plan)
    {
        foreach (var snap in team
                     .Where(s => s.Player.IsBot)
                     .OrderBy(s => s.Personality == BotPersonality.Sniper ? 0 : 1))
        {
            if (!snap.Player.IsValid)
                continue;
            var intended = PickLoadout(snap, plan);
            ApplyLoadout(snap.Player, intended);
        }
    }

    private List<PlayerSnap> CollectPlayers()
    {
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

            list.Add(new PlayerSnap
            {
                Player = player,
                Team = player.Team,
                StartMoney = startMoney,
                LeftoverRifle = prevWeapons.Any(WeaponCatalog.IsRifle) || prevWeapons.Any(WeaponCatalog.IsAwp),
                CurrentPrimary = valveGun,
                Personality = PersonalityFromGun(valveGun),
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
            if (_roundTypeByTeam.GetValueOrDefault(team) != BuyRound.FullBuy)
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

                int given = 0;
                while (given < 3 && poorIndex < poor.Count)
                {
                    var receiver = poor[poorIndex++];
                    if (!IsLiveCombatant(receiver) || gifted.Contains(receiver) || HasPrimaryWeapon(receiver))
                        continue;

                    bool allowAwp = !awpDropped && HasPrimaryWeapon(rich);
                    string gun = PickDroppedPrimary(team, rich.InGameMoneyServices.Account, allowAwp);
                    int price = WeaponCatalog.PriceOf(gun);
                    if (price <= 0 || rich.InGameMoneyServices.Account < price)
                        break;

                    receiver.GiveNamedItem(gun);
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

    private static string PickDroppedPrimary(CsTeam team, int money, bool allowAwp)
    {
        int awpPrice = WeaponCatalog.PriceOf("weapon_awp");
        if (allowAwp && awpPrice > 0 && money >= awpPrice)
            return "weapon_awp";

        if (team == CsTeam.CounterTerrorist)
            return Random.Shared.Next(2) == 0 ? "weapon_m4a1_silencer" : "weapon_m4a1";
        return "weapon_ak47";
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

    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int otMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;
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

    private int GetBotIndex(CCSPlayerController player)
    {
        if (!player.IsValid || !player.IsBot)
            return -1;
        int userId = player.UserId ?? -1;
        if (userId == -1)
            return -1;
        if (_botUserIdToIndex.TryGetValue(userId, out int idx))
            return idx;
        int newIdx = ++_botIndexCounter;
        _botUserIdToIndex[userId] = newIdx;
        return newIdx;
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
        int userId = player.UserId ?? -1;
        if (userId == -1)
            return;
        if (_botUserIdToIndex.Remove(userId, out int idx))
        {
            _prevWeapons.Remove(idx);
            _prevMoney.Remove(idx);
            _prevArmor.Remove(idx);
        }
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
            if (!player.IsValid || !player.IsBot)
                continue;
            int idx = GetBotIndex(player);
            if (idx == -1)
                continue;
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            _prevWeapons[idx] = WeaponCatalog.Weapons(pawn).ToList();
            _prevMoney[idx] = player.InGameMoneyServices?.Account ?? 0;
            _prevArmor[idx] = pawn.ArmorValue;
        }
    }

    private void ClearPreviousInventory(CCSPlayerController player)
    {
        if (!player.IsValid || !player.IsBot)
            return;
        int idx = GetBotIndex(player);
        if (idx == -1)
            return;
        _prevWeapons.Remove(idx);
        _prevArmor.Remove(idx);
    }

    private (List<string> Weapons, int Money, int Armor) PreviousInventory(CCSPlayerController player)
    {
        var weapons = new List<string>();
        int money = 0;
        int armor = 0;
        if (!player.IsValid || !player.IsBot)
            return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1)
            return (weapons, money, armor);
        if (IsFirstRoundOfHalf())
            return (weapons, money, armor);
        if (_prevWeapons.TryGetValue(idx, out var prev))
            weapons = prev;
        if (_prevMoney.TryGetValue(idx, out int m))
            money = m;
        if (_prevArmor.TryGetValue(idx, out int a))
            armor = a;
        return (weapons, money, armor);
    }
}
