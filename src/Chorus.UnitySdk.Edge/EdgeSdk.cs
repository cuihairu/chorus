using Chorus.UnitySdk;

namespace Chorus.UnitySdk.Edge
{
    /// <summary>The production assembly point (FIX-U3D-07): the SDK over the edge's
    /// WSS transport. Game code references this assembly alongside the SDK leaf and
    /// starts from here; the leaf stays transport-free.</summary>
    public static class EdgeSdk
    {
        /// <summary>The SDK wired to the Chorus edge's WSS transport.</summary>
        public static IChorusSdk Create() => new ChorusSdk(configuration => new EdgeChorusTransport(configuration));
    }
}
