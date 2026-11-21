namespace Chorus.GodotSdk
{
    /// <summary>
    /// The SDK's own wire message families (EK-1): the <see cref="GodotEnvelope.Type"/>
    /// vocabulary the transport speaks. These are the Godot transport's message names,
    /// not the server's IPC types — the golden test's byte-parity is about the codec,
    /// not this vocabulary. The four families the EK-2 golden set pins: login, sync,
    /// leaderboard and disconnect.
    /// </summary>
    public static class WireTypes
    {
        public const string Login = "sdk.login";
        public const string Sync = "sdk.sync";
        public const string Leaderboard = "sdk.leaderboard";
        public const string Disconnect = "sdk.disconnect";
    }
}
