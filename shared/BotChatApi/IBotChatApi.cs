using CounterStrikeSharp.API.Modules.Utils;

namespace BotChatApi;

public interface IBotChatApi
{
    int AbiVersion { get; }

    // Prevents two plugins from scheduling the same speaker concurrently.
    // Returns an ownership token, or zero when the speaker is unavailable.
    long TryReserveSpeaker(BotChatSpeaker speaker, float holdSeconds);

    void ReleaseSpeaker(BotChatSpeaker speaker, long reservationToken);

    // Resolves the immutable identity and sends immediately. True means the
    // line was actually broadcast, not merely queued.
    bool TrySendBotMessage(BotChatMessageRequest request, long reservationToken);
}

public readonly record struct BotChatSpeaker(
    ulong SteamId,
    int Slot,
    nint ControllerHandle,
    string DisplayName,
    CsTeam Team);

public sealed record BotChatMessageRequest(BotChatSpeaker Speaker, string Message);
