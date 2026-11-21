using System.Collections.Concurrent;
using Chorus.GodotSdk;

namespace Chorus.GodotSdk.Tests;

/// <summary>A scripted transport for the session-lifecycle lane: every seam method
/// answers a canned value and records the call, so the SDK's orchestration is testable
/// without a wire.</summary>
public sealed class FakeTransport : IChorusTransport
{
    public bool Connected { get; private set; }
    public bool Closed { get; private set; }
    public LoginIntent? LastLogin { get; private set; }
    public ResumeIntent? LastResume { get; private set; }
    public List<JoinMatchIntent> Joins { get; } = new();
    public ConcurrentQueue<ClientIntent> Sent { get; } = new();
    public bool FailConnect { get; set; }
    public Exception? ConnectError { get; set; }
    public LoginAnswer LoginAnswer { get; set; } = new()
    {
        StreamerId = "streamer-1",
        SessionId = "sess-1",
        SessionVersion = 1,
        AccessCredential = "cred-1",
        AccessExpiresAtUnixMs = 1_769_000_000_000,
        ResumeRegistered = true,
    };
    public string BindingId { get; set; } = "bind-1";
    public MatchTicket Ticket { get; set; } = new() { TicketId = "t-1", State = "Queued" };
    public JoinedRoom Room { get; set; } = new()
    {
        RoomId = "room-1",
        RoomEpoch = 3,
        Participation = "red",
        ModuleVersion = "module-1",
        ConfigHash = "cfg-1",
        TicketId = "t-1",
        ReservationId = "r-1",
    };
    public ISyncStream SyncStream { get; set; } = new StubStream();
    public IntentReply IntentReply { get; set; } = new() { CommandId = "c-1", RoomId = "room-1", Sequence = 1 };
    public LeaderboardAnswer BoardAnswer { get; set; } = new() { Found = true, ReadVersion = "v-1" };

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (FailConnect)
        {
            throw ConnectError ?? new InvalidOperationException("connect failed");
        }
        Connected = true;
        return Task.CompletedTask;
    }

    public Task<LoginAnswer> LoginAsync(LoginIntent intent, CancellationToken cancellationToken)
    {
        LastLogin = intent;
        return Task.FromResult(LoginAnswer);
    }

    public Task<string> StartGameplayAsync(string accessCredential, string gameId, string operationId,
        CancellationToken cancellationToken) => Task.FromResult(BindingId);

    public Task<MatchTicket> JoinMatchAsync(JoinMatchIntent intent, CancellationToken cancellationToken)
    {
        Joins.Add(intent);
        return Task.FromResult(Ticket);
    }

    public Task<MatchTicket> CancelMatchAsync(string operationId, string ticketId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MatchTicket { TicketId = ticketId, State = "Cancelled" });

    public Task<JoinedRoom> AwaitRoomAsync(CancellationToken cancellationToken) => Task.FromResult(Room);

    public Task<ISyncStream> SubscribeAsync(string roomId, long epoch, long lastAppliedTick,
        long lastAppliedStateVersion, CancellationToken cancellationToken) => Task.FromResult(SyncStream);

    public Task<IntentReply> SendIntentAsync(ClientIntent intent, CancellationToken cancellationToken)
    {
        Sent.Enqueue(intent);
        return Task.FromResult(IntentReply);
    }

    public Task<LoginAnswer> ResumeAsync(ResumeIntent intent, CancellationToken cancellationToken)
    {
        LastResume = intent;
        return Task.FromResult(LoginAnswer);
    }

    public Task<JoinedRoom> AwaitRejoinedRoomAsync(CancellationToken cancellationToken)
        => Task.FromResult(Room);

    public Task LeaveRoundAsync(string roomId, string operationId, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task LogoutAsync(string operationId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<LeaderboardAnswer> LeaderboardQueryAsync(LeaderboardQueryIntent intent,
        CancellationToken cancellationToken) => Task.FromResult(BoardAnswer);

    public Task CloseAsync()
    {
        Closed = true;
        return Task.CompletedTask;
    }

    private sealed class StubStream : ISyncStream
    {
        public SyncFrame Current { get; private set; } = new SyncFrame();
        public Task<bool> MoveNextAsync(CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
