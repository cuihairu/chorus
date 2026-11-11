using System.Reflection;
using System.Runtime.Versioning;
using Chorus.Client;
using Chorus.Protocol;
using Chorus.Rpc;
using Chorus.UnitySdk;
using BaselineStatus = Chorus.UnitySdk.BaselineStatus;

namespace Chorus.Engine.Tests;

/// <summary>
/// The unified Unity SDK contract (FIX-U3D-02) pinned from four sides: the assembly
/// stays a leaf on the Unity floor (netstandard2.1, no Chorus references, a public
/// configuration set with no room for secrets), every wire shape mirrors the server's
/// own contract field for field, the sync decision contract mirrors the reference
/// applier name for name, and the session surface carries mount, login, subscribe and
/// intents. The tests look into the assemblies with reflection, so any drift — a new
/// server field the Unity shape missed, a new configuration property that could carry
/// a secret, a renamed reason code — fails here instead of on a streamer's machine.
/// </summary>
public sealed class RedesignU3D02UnitySdkContractTests
{
    [Fact]
    public void TheContractAssemblyStaysALeafOnTheUnityFloor()
    {
        var assembly = typeof(IChorusSdk).Assembly;
        // The Unity floor (FIX-U3D-01): Douyin's LiveOpenSDK needs Unity 2020+ LTS, and a
        // streamer's Unity cannot even load a net10 assembly — a target bump must be a
        // conscious act that moves this assertion.
        var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>();
        Assert.Equal(".NETStandard,Version=v2.1", framework?.FrameworkName);
        // The leaf rule: the contract restates every wire shape, so the Unity package
        // never drags the server stack (gRPC runtime, data stores) into a game.
        var chorusReferences = assembly.GetReferencedAssemblies()
            .Where(name => name.Name?.StartsWith("Chorus", StringComparison.Ordinal) == true)
            .Select(name => name.Name)
            .ToList();
        Assert.Equal(Array.Empty<string>(), chorusReferences);

        // Layer 3 (FIX-U3D-01): the configuration surface is public material only — the
        // pinned set means a new property (a credential field, a "just for testing" key)
        // fails here before it can ship inside a game package.
        Assert.Equal(
            ["AppId", "Endpoint", "GameId", "Platform", "Role", "Tenant"],
            PublicProperties(typeof(SdkConfiguration)));
        // The mount answer is session context, not configuration: the platform's own
        // material rides it once, into the login exchange.
        Assert.Equal(
            ["AppId", "Platform", "Role", "SessionContextJson", "StreamerId", "UserId"],
            PublicProperties(typeof(PlatformMount)));
    }

    [Fact]
    public void TheWireShapesMirrorTheServerSyncContract()
    {
        // Each Unity shape against the server message it restates. The sets are equal
        // unless an entry below names a property the contract deliberately leaves out —
        // each exception is a design fact, not an oversight, and must be pinned to move.
        var pairs = new (Type Unity, Type Server, string[] Missing)[]
        {
            (typeof(SnapshotFrame), typeof(SnapshotMessage), []),
            (typeof(DeltaFrame), typeof(DeltaMessage), []),
            (typeof(RoundResultFrame), typeof(RoundResultMessage), []),
            (typeof(ResyncRequiredFrame), typeof(ResyncRequiredMessage), []),
            (typeof(CommandReceipt), typeof(CommandReceiptProto), []),
            (typeof(FrameEffect), typeof(FrameEffectProto), []),
            (typeof(LoginAnswer), typeof(LoginReadyReply), []),
            // The intent carries no tenant: the transport stamps the configuration's
            // tenant, so a game cannot cross tenants by composing its own intent.
            (typeof(ClientIntent), typeof(ClientInputRequest), ["TenantId"]),
            (typeof(IntentReply), typeof(ClientInputReply), []),
        };
        foreach (var (unity, server, missing) in pairs)
        {
            var expected = PublicProperties(server).Except(missing).Order().ToList();
            var actual = PublicProperties(unity);
            Assert.True(expected.SequenceEqual(actual),
                $"{unity.Name} does not mirror {server.Name}: contract {Describe(actual)} " +
                $"vs server {Describe(expected)} (deliberately absent: {string.Join(", ", missing)})");
        }

        // The union frame carries the oneof's members plus the kind discriminator.
        var union = PublicProperties(typeof(SyncFrame));
        Assert.Equal(
            ["Delta", "Kind", "ResyncRequired", "RoundResult", "Snapshot"],
            union);
        // The kind strings are the wire's own names, pinned so a rename cannot slip.
        Assert.Equal("snapshot", SyncFrameKind.Snapshot);
        Assert.Equal("delta", SyncFrameKind.Delta);
        Assert.Equal("round_result", SyncFrameKind.RoundResult);
        Assert.Equal("resync_required", SyncFrameKind.ResyncRequired);
    }

    [Fact]
    public void TheDecisionContractMirrorsTheReferenceApplier()
    {
        // The Unity applier (U3D-09) ports the reference applier's ordered rules; the
        // contract side must answer exactly what the server side answers, so a client
        // built on either implementation behaves identically. Names compare in order:
        // the decision kinds are an ordered ladder, not a bag.
        Assert.Equal(
            Enum.GetNames(typeof(BaselineDecisionKind)),
            Enum.GetNames(typeof(SyncDecisionKind)));

        var referenceReasons = ConstFields(typeof(BaselineReason));
        Assert.Equal(referenceReasons, ConstFields(typeof(SyncReasons)));
        // The reason values are the wire-stable strings both sides assert by name.
        Assert.Equal(
            ["CrossEpoch", "Duplicate", "Gap", "MissingBaseline", "NoDeltaContract",
                "ProtocolDeltaInvariant", "ProtocolEmptyMessage", "ProtocolModuleConfigMismatch",
                "ProtocolRoomMismatch", "ProtocolTickRegression", "ProtocolVersionRange",
                "Regression", "Resync"],
            referenceReasons.Keys.Order().ToList());

        Assert.Equal(
            ["Kind", "Reason", "TargetEpoch"],
            PublicProperties(typeof(SyncDecision)));
        Assert.Equal(
            ["ConfigHash", "Epoch", "HasBaseline", "ModuleVersion", "RoomId", "StateVersion",
                "Terminal", "Tick"],
            PublicProperties(typeof(BaselineStatus)));
    }

    [Fact]
    public void TheSessionSurfaceCarriesMountLoginSubscribeAndIntents()
    {
        // The entry: mount through the adapter, answer the session.
        var connect = Assert.Single(typeof(IChorusSdk).GetMethods(), m => m.Name == "ConnectAsync");
        Assert.Equal(typeof(Task<ChorusSession>), connect.ReturnType);
        Assert.Equal(
            [typeof(SdkConfiguration), typeof(IPlatformAdapter), typeof(CancellationToken)],
            connect.GetParameters().Select(p => p.ParameterType).ToList());

        // The resume entry (FIX-U3D-11): the dropped session's own facts rotate onto a
        // fresh session — no adapter takes part.
        var resume = Assert.Single(typeof(IChorusSdk).GetMethods(), m => m.Name == "ResumeAsync");
        Assert.Equal(typeof(Task<ChorusSession>), resume.ReturnType);
        Assert.Equal(
            [typeof(SdkConfiguration), typeof(string), typeof(byte[]), typeof(string), typeof(long),
                typeof(CancellationToken)],
            resume.GetParameters().Select(p => p.ParameterType).ToList());

        // The session: the login's answer, the gameplay binding, the two legs and the end.
        Assert.Equal(typeof(LoginAnswer), typeof(ChorusSession).GetProperty("Login")?.PropertyType);
        Assert.Equal(typeof(string), typeof(ChorusSession).GetProperty("BindingId")?.PropertyType);
        // The resume rotation: every session carries the secret that resumes it.
        Assert.Equal(typeof(byte[]), typeof(ChorusSession).GetProperty("ResumeCredential")?.PropertyType);
        var subscribe = Assert.Single(
            typeof(ChorusSession).GetMethods(), m => m.Name == "SubscribeAsync");
        Assert.Equal(typeof(Task<ISyncStream>), subscribe.ReturnType);
        Assert.Equal(
            [typeof(string), typeof(long), typeof(long), typeof(long), typeof(CancellationToken)],
            subscribe.GetParameters().Select(p => p.ParameterType).ToList());
        var send = Assert.Single(typeof(ChorusSession).GetMethods(), m => m.Name == "SendIntentAsync");
        Assert.Equal(typeof(Task<IntentReply>), send.ReturnType);
        Assert.Equal(
            [typeof(ClientIntent), typeof(CancellationToken)],
            send.GetParameters().Select(p => p.ParameterType).ToList());
        // The match and room-entry leg (FIX-U3D-08): queue, withdraw, and the atomic
        // entry answered only when the committed room and the seat's facts are together.
        var join = Assert.Single(typeof(ChorusSession).GetMethods(), m => m.Name == "JoinMatchAsync");
        Assert.Equal(typeof(Task<MatchTicket>), join.ReturnType);
        Assert.Equal(
            [typeof(string), typeof(string), typeof(CancellationToken)],
            join.GetParameters().Select(p => p.ParameterType).ToList());
        var cancel = Assert.Single(typeof(ChorusSession).GetMethods(), m => m.Name == "CancelMatchAsync");
        Assert.Equal(typeof(Task<MatchTicket>), cancel.ReturnType);
        Assert.Equal(
            [typeof(string), typeof(CancellationToken)],
            cancel.GetParameters().Select(p => p.ParameterType).ToList());
        var room = Assert.Single(typeof(ChorusSession).GetMethods(), m => m.Name == "AwaitRoomAsync");
        Assert.Equal(typeof(Task<JoinedRoom>), room.ReturnType);
        Assert.Equal(
            [typeof(CancellationToken)],
            room.GetParameters().Select(p => p.ParameterType).ToList());
        // The re-entry a resume lands (FIX-U3D-11): the participation record's room and
        // the seat's facts, with the snapshot behind it as the re-baseline.
        var rejoined = Assert.Single(typeof(ChorusSession).GetMethods(), m => m.Name == "AwaitRejoinedRoomAsync");
        Assert.Equal(typeof(Task<JoinedRoom>), rejoined.ReturnType);
        Assert.Equal(
            [typeof(CancellationToken)],
            rejoined.GetParameters().Select(p => p.ParameterType).ToList());
        Assert.NotNull(typeof(ChorusSession).GetMethod("DisconnectAsync"));

        // The stream reads pull-style: Unity's main loop drives it, the SDK never calls
        // game code from a transport thread.
        Assert.Equal(typeof(Task<bool>), typeof(ISyncStream).GetMethod("MoveNextAsync")?.ReturnType);
        Assert.Equal(typeof(SyncFrame), typeof(ISyncStream).GetProperty("Current")?.PropertyType);

        // The adapter seam and the applier the game hands frames to.
        Assert.Equal(typeof(string), typeof(IPlatformAdapter).GetProperty("PlatformName")?.PropertyType);
        Assert.Equal(typeof(Task<PlatformMount>), typeof(IPlatformAdapter).GetMethod("MountAsync")?.ReturnType);
        Assert.NotNull(typeof(IPlatformAdapter).GetMethod("ShutdownAsync"));
        Assert.Equal(typeof(SyncDecision), typeof(ISyncApplier).GetMethod("Decide")?.ReturnType);
        Assert.Equal(typeof(BaselineStatus), typeof(ISyncApplier).GetProperty("Status")?.PropertyType);
    }

    /// <summary>The type's public instance property names, sorted — the contract's own face.</summary>
    private static List<string> PublicProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .Order()
            .ToList();

    /// <summary>The type's public constant string fields, name to value.</summary>
    private static Dictionary<string, string> ConstFields(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (Name: field.Name, Value: (string)field.GetRawConstantValue()!))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal);

    private static string Describe(List<string> names) => "[" + string.Join(", ", names) + "]";
}
