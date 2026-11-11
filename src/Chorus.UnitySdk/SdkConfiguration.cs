using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Chorus.UnitySdk
{
    /// <summary>
    /// The Unity SDK's public configuration (FIX-U3D-01 layer 3): everything here is safe
    /// to ship inside a game package — platform choice, app id, tenant and game names and
    /// the public Chorus endpoint. The server-side secrets (app_secret, application
    /// private keys, server access tokens) live in Chorus server configuration and never
    /// appear on this surface; the configuration wizard (FIX-U3D-04) enforces that split
    /// and the contract test pins the property set against silent additions.
    /// </summary>
    public sealed class SdkConfiguration
    {
        /// <summary>The platform the game is built for: "douyin" or "kuaishou".</summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>The platform's public app id — the one identifier both platforms publish to their game.</summary>
        public string AppId { get; set; } = string.Empty;

        /// <summary>The Chorus tenant the application belongs to.</summary>
        public string Tenant { get; set; } = string.Empty;

        /// <summary>The Chorus game the session joins.</summary>
        public string GameId { get; set; } = string.Empty;

        /// <summary>The streamer gateway's public endpoint: login, subscribe and intents ride it.</summary>
        public string Endpoint { get; set; } = string.Empty;

        /// <summary>Which seat this build speaks from: "streamer" or "player".</summary>
        public string Role { get; set; } = string.Empty;
    }
}
