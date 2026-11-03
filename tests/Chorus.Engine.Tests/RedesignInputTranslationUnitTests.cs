using System.Text.Json;
using Chorus.ExampleGame;
using Chorus.GameServer;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-INPUT-08, the configured mapping half: a platform delivery in the gateway's
/// normalized event shape (camelCase, kind as its stable numeric code) becomes this module
/// family's command shape before the tick, a gift's value lands as its recipient faction's
/// score effect, and a payload that is not a platform shape passes through untouched. The
/// no-translator twin documents the honest boundary FIX-INPUT-06 drew: without the mapping
/// the same gift reaches the frame and the client delta but scores nothing. FIX-INPUT-09's
/// like half is here too — a like maps to its click count, read from the reducer's merged
/// count convention, never to a per-callback assumption.
/// </summary>
public sealed class RedesignInputTranslationUnitTests
{
    private const string GiftWire = """
        {"eventId":"wire-gift-1","platform":"simulator","sessionId":"live-1","userId":"user-1",
         "kind":3,"giftId":"g1","giftCount":1,"giftValue":100}
        """;

    [Fact]
    public void AGiftPayloadMapsToTheModuleCommandAndOtherPlatformKindsDoNot()
    {
        var translator = new AudienceEventModuleInputTranslator();

        var command = translator.Translate(GiftWire);
        using (var document = JsonDocument.Parse(command!))
        {
            Assert.Equal("gift", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal(100, document.RootElement.GetProperty("amount").GetInt32());
        }

        // A gift with no stated value scores nothing deterministically.
        Assert.Null(translator.Translate(
            """{"eventId":"g","platform":"simulator","sessionId":"s","userId":"u","kind":3}"""));
        // Comments take no command from this family.
        Assert.Null(translator.Translate(
            """{"eventId":"c","platform":"simulator","sessionId":"s","userId":"u","kind":1}"""));
    }

    [Fact]
    public void ALikePayloadMapsToItsClickCountAndCommentsDoNot()
    {
        var translator = new AudienceEventModuleInputTranslator();

        // A single like, no stated count: one physical click — the count is one, not the
        // callback count in the report.
        var single = translator.Translate(
            """{"eventId":"l1","platform":"simulator","sessionId":"s","userId":"u","kind":2}""");
        using (var document = JsonDocument.Parse(single!))
        {
            Assert.Equal("like", document.RootElement.GetProperty("kind").GetString());
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
        }

        // The reducer's window merge states the merged click count in text (FIX-INPUT-09):
        // one callback standing for five clicks reports five clicks, not one callback.
        var merged = translator.Translate(
            """{"eventId":"l2","platform":"simulator","sessionId":"s","userId":"u","kind":2,"text":"5"}""");
        using (var document = JsonDocument.Parse(merged!))
            Assert.Equal(5, document.RootElement.GetProperty("count").GetInt32());

        // A non-numeric text is not a count: one click, deterministically.
        var odd = translator.Translate(
            """{"eventId":"l3","platform":"simulator","sessionId":"s","userId":"u","kind":2,"text":"many"}""");
        using (var document = JsonDocument.Parse(odd!))
            Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ANonPlatformPayloadPassesThroughUntouched()
    {
        var translator = new AudienceEventModuleInputTranslator();

        // A raw module command (the demo path) has no platform markers: pass through.
        const string command = """{"kind":"gift","amount":30}""";
        Assert.Equal(command, translator.Translate(command));
        // A boost command is not an AudienceEvent shape either.
        const string boost = """{"action":"boost","level":3}""";
        Assert.Equal(boost, translator.Translate(boost));
        // Malformed JSON is not a platform payload: hand it on unchanged, as before.
        Assert.Equal("{not json", translator.Translate("{not json"));
    }

    [Fact]
    public async Task ATranslatedGiftScoresItsFactionOnTheCommittedTick()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 1, new AudienceEventModuleInputTranslator());
        var subscriber = actor.Subscribe(0, 1);

        Assert.True(actor.TryHand("wire-gift-1", "binding-seat", GiftWire, "simulator"));
        actor.Wake();
        actor.Start();

        var delta = await NextAsync(subscriber);
        Assert.NotNull(delta);
        Assert.Equal(SyncMessage.KindOneofCase.Delta, delta.KindCase);
        Assert.Contains("wire-gift-1", delta.Delta.InputEventIds);
        var score = Assert.Single(delta.Delta.Effects, effect => effect.Kind == "score");
        using (var effect = JsonDocument.Parse(score.PayloadJson))
        {
            Assert.Equal("red", effect.RootElement.GetProperty("faction").GetString());
            Assert.Equal(100, effect.RootElement.GetProperty("delta").GetInt64());
            Assert.Equal(100, effect.RootElement.GetProperty("total").GetInt64());
        }

        var result = await ResultAsync(subscriber);
        using var document = JsonDocument.Parse(result!.RoundResult.ResultJson);
        Assert.Equal(100L, ScoreOf(document, "red"));
        Assert.Equal(0L, ScoreOf(document, "blue"));

        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    [Fact]
    public async Task WithoutTheTranslatorTheSameGiftConsumesButScoresNothing()
    {
        var store = new ScriptedFrameStore();
        var actor = NewRoom(store, durationTicks: 1);
        var subscriber = actor.Subscribe(0, 1);

        Assert.True(actor.TryHand("wire-gift-1", "binding-seat", GiftWire, "simulator"));
        actor.Wake();
        actor.Start();

        var delta = await NextAsync(subscriber);
        Assert.NotNull(delta);
        // The frame still records the input — the client sees the receipt — but the module
        // does not recognize the platform shape, so no score effect and no points.
        Assert.Contains("wire-gift-1", delta.Delta.InputEventIds);
        Assert.DoesNotContain(delta.Delta.Effects, effect => effect.Kind == "score");

        var result = await ResultAsync(subscriber);
        using var document = JsonDocument.Parse(result!.RoundResult.ResultJson);
        Assert.Equal(0L, ScoreOf(document, "red"));

        await actor.DisposeAsync();
        subscriber.Dispose();
    }

    private static long ScoreOf(JsonDocument result, string faction) =>
        result.RootElement.GetProperty("ranking").EnumerateArray()
            .Single(entry => entry.GetProperty("faction").GetString() == faction)
            .GetProperty("score").GetInt64();

    private static async Task<SyncMessage?> ResultAsync(RoomSubscriber subscriber)
    {
        while (true)
        {
            var message = await NextAsync(subscriber);
            if (message is null || message.KindCase == SyncMessage.KindOneofCase.RoundResult)
                return message;
        }
    }

    private static async Task<SyncMessage?> NextAsync(RoomSubscriber subscriber)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { return await subscriber.NextAsync(timeout.Token); }
        catch (OperationCanceledException) { return null; }
    }

    private static RoomActor NewRoom(ScriptedFrameStore store, long durationTicks,
        IRoomInputTranslator? translator = null)
    {
        var module = new ExampleGameModuleV2();
        var participants = new List<GameParticipant>
        {
            new("host-seat", GameStartConfig.RedFaction), new("guest-seat", GameStartConfig.BlueFaction)
        };
        var initial = module.CreateInitialState(new GameStartConfig(durationTicks, 777, participants));
        var bindings = new Dictionary<string, string> { ["binding-seat"] = "host-seat" };
        return new RoomActor(new ScriptedLifecycle(), store, module, "game-seat", "room-seat",
            "game-a", 1, "cfg-seat", initial, bindings, inputTranslator: translator);
    }

    /// <summary>The data plane as the actor sees it: every commit answers a receipt, the
    /// inbox read is empty and counted (the seat path must not need one).</summary>
    private sealed class ScriptedFrameStore : FrameStore.FrameStoreClient
    {
        public override AsyncUnaryCall<ReadInboxReply> ReadInboxAsync(ReadInboxRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var reply = Task.FromResult(new ReadInboxReply());
            return new AsyncUnaryCall<ReadInboxReply>(reply,
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }

        public override AsyncUnaryCall<FrameReceiptReply> CommitFrameAsync(CommitFrameRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            var receipt = new FrameReceiptReply
            {
                CommitId = request.CommitId, RoomId = request.RoomId, Tick = request.PrevTick + 1,
                StateVersion = request.PrevStateVersion + 1
            };
            return new AsyncUnaryCall<FrameReceiptReply>(Task.FromResult(receipt),
                Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
        }
    }

    private sealed class ScriptedLifecycle : IRoomLifecycle
    {
        public Task SuspendRoomAsync(string gameId, string roomId, string ownerInstanceId, long epoch,
            string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SettleRoomAsync(string gameId, string roomId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReportEndAsync(string gameId, string roomId, long finalTick, long resultVersion,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
