namespace Chorus.Messaging.Ipc;

public static class IpcMessageTypes
{
    public const string GatewayConnect = "gw.connect";
    public const string GatewayDisconnect = "gw.disconnect";
    public const string GatewayAck = "gw.ack";
    public const string GatewayAudienceEvent = "gw.audienceEvent";

    public const string EngineSnapshot = "eng.snapshot";
    public const string EngineWelcome = "eng.welcome";
}
