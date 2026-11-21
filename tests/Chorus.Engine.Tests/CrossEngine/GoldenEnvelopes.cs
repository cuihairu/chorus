namespace Chorus.Engine.Tests.CrossEngine;

/// <summary>
/// The golden Envelope set (EK-2): the canonical wire messages the cross-engine
/// consistency test pins. Four families — login, sync, leaderboard, disconnect — each
/// a representative payload with the fields a real frame carries, including a base64
/// byte-array field and a null field (the two places a serializer change shows first).
/// Both engines' codecs must serialize these to identical bytes and deserialize the
/// bytes back to identical fields; the set is the drift detector, so the bodies are
/// literal JSON and never regenerated from a serializer.
/// </summary>
public sealed record GoldenEnvelope(
    string Name,
    string Type,
    string MessageId,
    string SessionId,
    long CreatedTimeMs,
    string BodyJson,
    string? SourceEventId,
    long? AuthorityEpoch);

public static class GoldenEnvelopes
{
    public static readonly GoldenEnvelope[] Set =
    {
        // The login frame: the mount exchange's request, with the resume secret riding
        // as base64 (the byte-array field) and the launch credential as escaped JSON.
        new GoldenEnvelope(
            Name: "login",
            Type: "sdk.login",
            MessageId: "msg-login-0001",
            SessionId: "sess-golden-1",
            CreatedTimeMs: 1_769_000_000_000,
            BodyJson: "{\"platform\":\"douyin\",\"appId\":\"app-1\"," +
                      "\"launchCredential\":\"{\\\"token\\\":\\\"t-1\\\"}\"," +
                      "\"loginAttemptId\":\"attempt-1001\"," +
                      "\"nextResumeSecret\":\"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw8=\"}",
            SourceEventId: null,
            AuthorityEpoch: null),

        // The sync frame: one snapshot on the room's authoritative chain, with the
        // module state riding opaque (base64) and the schema version naming it.
        new GoldenEnvelope(
            Name: "sync",
            Type: "sdk.sync",
            MessageId: "msg-sync-0002",
            SessionId: "sess-golden-1",
            CreatedTimeMs: 1_769_000_001_000,
            BodyJson: "{\"kind\":\"snapshot\"," +
                      "\"snapshot\":{\"roomId\":\"room-1\",\"epoch\":3,\"tick\":5," +
                      "\"stateVersion\":2,\"moduleVersion\":\"module-1\",\"configHash\":\"cfg-1\"," +
                      "\"statePayload\":\"AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHw8=\"," +
                      "\"stateSchemaVersion\":1}}",
            SourceEventId: "evt-golden-2",
            AuthorityEpoch: 3),

        // The leaderboard frame: one top read, with the optional period/scope/cursor
        // fields absent (the null-skip contract) and the subject naming the row filter.
        new GoldenEnvelope(
            Name: "leaderboard",
            Type: "sdk.leaderboard",
            MessageId: "msg-board-0003",
            SessionId: "sess-golden-1",
            CreatedTimeMs: 1_769_000_002_000,
            BodyJson: "{\"operationId\":\"lb-1003\",\"kind\":\"top\",\"boardId\":\"board-1\"," +
                      "\"subject\":0,\"maxEntries\":10,\"window\":0}",
            SourceEventId: null,
            AuthorityEpoch: null),

        // The disconnect frame: the session end, with the reason the wire phrased.
        new GoldenEnvelope(
            Name: "disconnect",
            Type: "sdk.disconnect",
            MessageId: "msg-bye-0004",
            SessionId: "sess-golden-1",
            CreatedTimeMs: 1_769_000_003_000,
            BodyJson: "{\"operationId\":\"logout-1004\",\"reason\":\"logout\"}",
            SourceEventId: "evt-golden-4",
            AuthorityEpoch: 1),
    };
}
