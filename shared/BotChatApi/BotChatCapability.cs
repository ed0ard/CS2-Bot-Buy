using CounterStrikeSharp.API.Core.Capabilities;

namespace BotChatApi;

public static class BotChatCapability
{
    public static readonly PluginCapability<IBotChatApi> Cap = new("botchat:api");
}
