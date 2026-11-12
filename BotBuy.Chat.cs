using System.Text.RegularExpressions;
using BotChatApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BotBuyPatch;

internal sealed class BotBuyMessages
{
    public List<string> DropWeapon { get; set; } = [];
}

public sealed partial class BotBuyPatch
{
    private const string DropChatLanguage = "zh-CN";

    private List<string> _dropMessages = [];
    private bool _dropApiWarningLogged;

    public override void Load(bool hotReload)
    {
        LoadDropMessages();
    }

    private void LoadDropMessages()
    {
        try
        {
            _dropMessages = LoadDropPool(ModuleDirectory, DropChatLanguage);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BotBuy] drop chat disabled: {ex.Message}");
            _dropMessages = [];
        }
    }

    private static List<string> LoadDropPool(string moduleDirectory, string language)
    {
        if (!LanguageNamePattern().IsMatch(language))
            throw new InvalidDataException($"Invalid BotBuy language name '{language}'.");

        string path = Path.Combine(moduleDirectory, "lang", $"{language}.yml");
        if (!File.Exists(path))
            throw new FileNotFoundException($"BotBuy language file was not found: {path}", path);

        var messages = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Deserialize<BotBuyMessages>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"BotBuy language file is empty: {path}");

        var pool = messages.DropWeapon ?? [];
        if (pool.Count == 0)
            throw new InvalidDataException($"BotBuy message pool 'drop_weapon' is empty in {path}.");

        for (int i = 0; i < pool.Count; i++)
        {
            string line = pool[i]?.Trim() ?? "";
            if (line.Length == 0)
                throw new InvalidDataException($"BotBuy message pool 'drop_weapon' contains an empty entry in {path}.");
            pool[i] = line;
        }

        return pool;
    }

    private void AnnounceDrop(CCSPlayerController speaker, CCSPlayerController receiver, int dropIndex)
    {
        if (_dropMessages.Count == 0 || !speaker.IsValid || !speaker.IsBot || speaker.IsHLTV)
            return;
        if (speaker.HasBeenControlledByPlayerThisRound)
            return;
        if (!IsHumanDropReceiver(receiver) || receiver.Team != speaker.Team)
            return;

        float delay = 0.4f + (float)Random.Shared.NextDouble() * 0.8f + dropIndex * 0.8f;
        int speakerSlot = speaker.Slot;
        nint speakerHandle = speaker.Handle;
        int receiverSlot = receiver.Slot;
        nint receiverHandle = receiver.Handle;
        int receiverUserId = receiver.UserId ?? -1;
        var identity = new BotChatSpeaker(
            speaker.SteamID, speaker.Slot, speaker.Handle, SanitizeChat(speaker.PlayerName), speaker.Team);
        var api = GetBotChatApi();
        if (api == null || api.AbiVersion != 1)
        {
            AddTimer(delay, () =>
            {
                if (!TryFormatLiveDropLine(speakerSlot, speakerHandle, receiverSlot, receiverHandle, receiverUserId, out string fallbackLine, out var liveSpeaker))
                    return;
                BroadcastDropFallback(SanitizeChat(liveSpeaker.PlayerName), liveSpeaker.Team, fallbackLine);
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        long reservation = api.TryReserveSpeaker(identity, delay + 0.5f);
        if (reservation == 0)
            return;

        AddTimer(delay, () =>
        {
            try
            {
                if (!TryFormatLiveDropLine(speakerSlot, speakerHandle, receiverSlot, receiverHandle, receiverUserId, out string line, out _))
                    return;
                GetBotChatApi()?.TrySendBotMessage(new BotChatMessageRequest(identity, line), reservation);
            }
            finally
            {
                api.ReleaseSpeaker(identity, reservation);
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }

    private bool TryFormatLiveDropLine(
        int speakerSlot,
        nint speakerHandle,
        int receiverSlot,
        nint receiverHandle,
        int receiverUserId,
        out string message,
        out CCSPlayerController speaker)
    {
        message = "";
        speaker = null!;

        var liveSpeaker = Utilities.GetPlayerFromSlot(speakerSlot);
        if (liveSpeaker == null || !liveSpeaker.IsValid || liveSpeaker.Handle != speakerHandle)
            return false;
        if (!liveSpeaker.IsBot || liveSpeaker.IsHLTV || liveSpeaker.HasBeenControlledByPlayerThisRound)
            return false;

        var liveReceiver = Utilities.GetPlayerFromSlot(receiverSlot);
        if (!IsHumanDropReceiver(liveReceiver)
            || liveReceiver!.Handle != receiverHandle
            || (liveReceiver.UserId ?? -1) != receiverUserId
            || liveReceiver.Team != liveSpeaker.Team)
            return false;

        message = FormatDropLine(
            _dropMessages[Random.Shared.Next(_dropMessages.Count)],
            liveReceiver.PlayerName,
            liveSpeaker.PlayerName);
        if (message.Length == 0)
            return false;

        speaker = liveSpeaker;
        return true;
    }

    private IBotChatApi? GetBotChatApi()
    {
        try
        {
            return BotChatCapability.Cap.Get();
        }
        catch
        {
            if (!_dropApiWarningLogged)
            {
                _dropApiWarningLogged = true;
                Console.WriteLine("[BotBuy] BotChat capability is unavailable; drop lines use local broadcast");
            }
            return null;
        }
    }

    private static void BroadcastDropFallback(string name, CsTeam team, string message)
    {
        Server.PrintToChatAll($" {ChatColors.ForTeam(team)}{name}{ChatColors.Default}: {message}");
    }

    private static string FormatDropLine(string text, string other, string self) =>
        text.Replace("{other}", SanitizeChat(other)).Replace("{self}", SanitizeChat(self));

    private static string SanitizeChat(string value) =>
        new(value.Where(c => c >= 32 && c != '\n' && c != '\r' && c != '"' && c != ';').ToArray());

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex LanguageNamePattern();
}
