using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chorus.Redesign.Rpc;
using Chorus.Redesign.StreamerGateway.Messages;
using Grpc.Core;

namespace Chorus.Redesign.StreamerGateway;

/// <summary>
/// The public /ws face (contracts 3). One text frame is one JSON message. The hello is the
/// first frame and gates everything — a connection that never completes it is closed within
/// the pre-auth window, and a hello with an unknown protocol version is refused before any
/// other byte is read. Frames above the size cap close the connection (transport limits are
/// enforced at the transport). After the hello this loop carries the session round-trips:
/// login, resume and logout reach the StreamerSession boundary, startGameplay mounts the
/// session and joinQueue/cancelQueue drive the mount's match ticket, session-scored uplinks
/// before a session are refused with no_session, and a session-replaced notice can arrive on
/// the internal face at any moment and take the connection down through the same serialized
/// downlink lane. The routing watch pushes the match notice when the mount's round starts.
/// </summary>
public sealed class EdgeWs(
    EdgeOptions options, EdgeSessionRegistry registry, TenantApplicationMap tenantApplications,
    EdgeSessionClient sessionClient, string gatewayInstanceId,
    EdgeQueueClient? queueClient = null, EdgeRoutingMonitor? routingMonitor = null,
    EdgeRoundClient? roundClient = null, EdgeRoomFanout? fanout = null,
    EdgeInputClient? inputClient = null)
{
    private const int ReceiveChunkBytes = 8 * 1024;

    /// <summary>RFC 6455 1009 "message too big": the .NET enum stops below it, so the raw
    /// code is sent as-is — the client sees the standard status, not a borrowed one.</summary>
    private const WebSocketCloseStatus TooLargeCloseStatus = (WebSocketCloseStatus)1009;

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new EdgeConnection(Guid.NewGuid().ToString("N"), gatewayInstanceId, options);
        if (!registry.TryOpen(connection))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, EdgeErrorCodes.GatewayFull);
            return;
        }
        // From here on every downlink — dispatch reply or replacement notice — flows through
        // the connection's serialized lane, so the socket never sees concurrent sends. The
        // outbox pump drains through the same lane, so room frames queue behind every direct
        // send instead of interleaving with them.
        connection.AttachTransport(
            (frame, cancellation) => SendFrameAsync(socket, frame, cancellation),
            (status, reason) => CloseAsync(socket, status, reason));
        connection.StartOutboxPump();
        try
        {
            // The hello has its own deadline: an accepted socket that stays silent is closed
            // when the window lapses — the edge holds no unauthenticated connections.
            using var gate = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            gate.CancelAfter(options.PreAuthTimeout);
            var helloText = await ReceiveTextAsync(socket, gate.Token);
            if (helloText is null) return; // the client left before speaking.
            if (!await TryAcceptHelloAsync(connection, helloText)) return;
            connection.MarkHelloAccepted();
            await DispatchLoopAsync(socket, connection, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client or the host went away; nothing to answer.
        }
        catch (OperationCanceledException)
        {
            // The pre-auth window lapsed while the receive was still pending. A canceled
            // receive leaves the managed websocket past a graceful close handshake — the
            // socket aborts, and the client sees the transport drop. What the contract
            // guarantees is that the edge holds no unauthenticated connection open, not the
            // close code it cannot send anymore.
            socket.Abort();
        }
        catch (EdgeMessageTooLargeException)
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.MessageTooLarge,
                Message = "The frame exceeds the message size limit."
            }, CancellationToken.None);
            await CloseAsync(socket, TooLargeCloseStatus, EdgeErrorCodes.MessageTooLarge);
        }
        catch (WebSocketException)
        {
            // A transport failure mid-frame: the connection is gone, close is not deliverable.
        }
        catch (JsonException)
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "The frame is not a valid protocol message."
            }, CancellationToken.None);
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, EdgeErrorCodes.InvalidMessage);
        }
        finally
        {
            // A closing connection stops hearing about rooms before it leaves the registry.
            // The fan-out detaches first — the outbox's producer — then the pump drains and
            // dies, so no frame is queued onto a pump that is already gone.
            routingMonitor?.Unsubscribe(connection.ConnectionId);
            fanout?.Leave(connection.ConnectionId);
            await connection.StopOutboxPumpAsync();
            registry.Close(connection);
        }
    }

    private async Task<bool> TryAcceptHelloAsync(EdgeConnection connection, string helloText)
    {
        var hello = JsonSerializer.Deserialize<EdgeHello>(helloText, EdgeJson.Options)
            ?? throw new JsonException();
        if (hello.ProtocolVersion != EdgeProtocolVersion.Current)
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.ProtocolMismatch,
                Message = $"Protocol version {hello.ProtocolVersion} is not supported."
            }, CancellationToken.None);
            await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation, EdgeErrorCodes.ProtocolMismatch);
            return false;
        }
        return true;
    }

    private async Task DispatchLoopAsync(WebSocket socket, EdgeConnection connection,
        CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, cancellation);
            if (text is null) return; // the client closed gracefully.
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var type = typeElement.GetString()!;
            var verdict = connection.Admit(type);
            if (verdict == EdgeRateVerdict.Dropped) continue;
            if (verdict == EdgeRateVerdict.Rejected)
            {
                await connection.SendJsonAsync(new EdgeError
                {
                    Code = EdgeErrorCodes.RateLimited,
                    Message = $"The {type} budget for this window is exhausted."
                }, cancellation);
                continue;
            }
            if (EdgeTypes.RequiresSession(type) && !connection.HasSession)
            {
                // No session, no gameplay traffic (contracts 3.1) — the refusal is visible
                // and the connection stays open for a login.
                await connection.SendJsonAsync(new EdgeError
                {
                    Code = EdgeErrorCodes.NoSession,
                    Message = "Login or resume before sending session messages."
                }, cancellation);
                continue;
            }
            switch (type)
            {
                case EdgeTypes.Ping:
                    await connection.SendJsonAsync(new EdgePong
                    {
                        ServerTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    }, cancellation);
                    break;
                case EdgeTypes.LoginWithPlatformLaunch:
                    await HandleLoginAsync(connection, root, cancellation);
                    break;
                case EdgeTypes.Resume:
                    await HandleResumeAsync(connection, root, cancellation);
                    break;
                case EdgeTypes.Logout:
                    if (await HandleLogoutAsync(connection, root, cancellation)) return;
                    break;
                case EdgeTypes.StartGameplay:
                    await HandleStartGameplayAsync(connection, root, cancellation);
                    break;
                case EdgeTypes.JoinQueue:
                    await HandleJoinQueueAsync(connection, root, cancellation);
                    break;
                case EdgeTypes.CancelQueue:
                    await HandleCancelQueueAsync(connection, root, cancellation);
                    break;
                case EdgeTypes.Ack:
                {
                    // The applied position is the connection's re-baseline fact (contracts
                    // 3.1): recorded on the room this connection plays in, never answered —
                    // an acknowledgement has no downlink of its own.
                    var ack = root.Deserialize<EdgeAck>(EdgeJson.Options) ?? throw new JsonException();
                    connection.MarkApplied(ack.RoomId, ack.LastAppliedTickId, ack.StateVersion);
                    break;
                }
                case EdgeTypes.RequestSnapshot:
                {
                    var request = root.Deserialize<EdgeRequestSnapshot>(EdgeJson.Options)
                        ?? throw new JsonException();
                    // Only a room this connection sits in can be re-baselined; anything else
                    // addresses a room the server never put it in.
                    if (fanout is null || !fanout.RequestSnapshot(connection.ConnectionId, request.RoomId))
                    {
                        await connection.SendJsonAsync(new EdgeError
                        {
                            Code = EdgeErrorCodes.InvalidMessage,
                            Message = "That room is not active on this connection."
                        }, cancellation);
                    }
                    break;
                }
                case EdgeTypes.GameCommand:
                {
                    var command = root.Deserialize<EdgeGameCommand>(EdgeJson.Options)
                        ?? throw new JsonException();
                    await HandleGameCommandAsync(connection, command, cancellation);
                    break;
                }
                case EdgeTypes.LeaveRound:
                {
                    var leave = root.Deserialize<EdgeLeaveRound>(EdgeJson.Options)
                        ?? throw new JsonException();
                    await HandleLeaveRoundAsync(connection, leave, cancellation);
                    break;
                }
                default:
                    // A closed type set (contracts 3.1): what is not defined is a violation,
                    // not a future message to skip over.
                    await connection.SendJsonAsync(new EdgeError
                    {
                        Code = EdgeErrorCodes.InvalidMessage,
                        Message = $"Message type {type} is not part of the protocol."
                    }, cancellation);
                    await connection.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                        EdgeErrorCodes.InvalidMessage);
                    return;
            }
        }
    }

    private async Task HandleLoginAsync(EdgeConnection connection, JsonElement root,
        CancellationToken cancellation)
    {
        var login = root.Deserialize<EdgeLoginWithPlatformLaunch>(EdgeJson.Options) ?? throw new JsonException();
        if (!tenantApplications.TryResolve(login.AppId, out var tenant, out _))
        {
            // The edge cannot name a tenant for an application it does not serve; the login
            // never leaves this process.
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "The application is not mapped at this gateway."
            }, cancellation);
            return;
        }
        if (login.NextResumeSecret is not { Length: >= 32 } secret)
            throw new JsonException();
        try
        {
            var ready = await sessionClient.LoginAsync(tenant, login.Platform ?? "", login.AppId,
                login.LaunchCredential, login.LoginAttemptId ?? "", secret, connection.ConnectionId,
                cancellation);
            connection.MarkSession(tenant, ready.SessionId, ready.StreamerId, ready.AccessCredential);
            await connection.SendJsonAsync(new EdgeLoginReady
            {
                StreamerId = ready.StreamerId,
                SessionId = ready.SessionId,
                SessionVersion = ready.SessionVersion,
                AccessCredential = ready.AccessCredential,
                AccessExpiresAt = ready.AccessExpiresAtUnixMs,
                ResumeRegistered = ready.ResumeRegistered
            }, cancellation);
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, login.LoginAttemptId, cancellation);
        }
    }

    private async Task HandleResumeAsync(EdgeConnection connection, JsonElement root,
        CancellationToken cancellation)
    {
        var resume = root.Deserialize<EdgeResume>(EdgeJson.Options) ?? throw new JsonException();
        if (resume.ResumeCredential is not { Length: >= 32 } previous
            || resume.NextResumeSecret is not { Length: >= 32 } next)
            throw new JsonException();
        try
        {
            var ready = await sessionClient.ResumeAsync(resume.PreviousSessionId,
                resume.ResumeOperationId, previous, next, connection.ConnectionId, cancellation);
            connection.MarkSession("", ready.SessionId, ready.StreamerId, ready.AccessCredential);
            await connection.SendJsonAsync(new EdgeLoginReady
            {
                StreamerId = ready.StreamerId,
                SessionId = ready.SessionId,
                SessionVersion = ready.SessionVersion,
                AccessCredential = ready.AccessCredential,
                AccessExpiresAt = ready.AccessExpiresAtUnixMs,
                ResumeRegistered = ready.ResumeRegistered
            }, cancellation);
            // A resume mid-round re-enters its room on the server's word (contracts 3.1: the
            // participation record decides, the client's lastSeenRoomId is diagnostics). The
            // declared applied version seeds the re-baseline the snapshot continues from — a
            // resume carries no tick, so the answer is always a snapshot.
            connection.SeedAppliedFromResume(resume.LastSeenRoomId ?? "",
                resume.LastAppliedStateVersion ?? 0);
            await TryRejoinRoomAsync(connection, ready.StreamerId);
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, null, cancellation);
        }
    }

    private async Task<bool> HandleLogoutAsync(EdgeConnection connection, JsonElement root,
        CancellationToken cancellation)
    {
        var logout = root.Deserialize<EdgeLogout>(EdgeJson.Options) ?? throw new JsonException();
        if (!connection.HasSession)
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.NoSession,
                Message = "There is no session to end."
            }, cancellation);
            return false;
        }
        try
        {
            // The revocation is durable before the close: a client that misses the close and
            // retries learns from the refusal that the session is already gone.
            await sessionClient.LogoutAsync(connection.TenantId!, connection.AccessCredential!,
                connection.ConnectionId, logout.OperationId ?? "", cancellation);
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, EdgeTypes.Logout);
            return true;
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, logout.OperationId, cancellation);
            return false;
        }
    }

    private async Task HandleStartGameplayAsync(EdgeConnection connection, JsonElement root,
        CancellationToken cancellation)
    {
        var start = root.Deserialize<EdgeStartGameplay>(EdgeJson.Options) ?? throw new JsonException();
        try
        {
            // The game id is the client's choice; everything else about the actor comes from
            // the session the login established (contracts 3.1: no uplink identity is trusted).
            var status = await sessionClient.StartGameplayAsync(connection.TenantId!,
                connection.AccessCredential!, start.GameId, start.OperationId, cancellation);
            connection.MarkGameplay(status.BindingId, status.GameId, start.RulesVersion);
            await connection.SendJsonAsync(new EdgeGameplayStatus
            {
                BindingId = status.BindingId,
                Generation = status.Generation,
                State = status.State,
                Reason = status.Duplicate ? "duplicate" : null
            }, cancellation);
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, start.OperationId, cancellation);
        }
    }

    private async Task HandleJoinQueueAsync(EdgeConnection connection, JsonElement root,
        CancellationToken cancellation)
    {
        var join = root.Deserialize<EdgeJoinQueue>(EdgeJson.Options) ?? throw new JsonException();
        if (!connection.HasGameplay)
        {
            // A queue ticket is per mount; without a startGameplay there is no mount to queue.
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "Start gameplay before joining a queue.",
                OperationId = join.OperationId
            }, cancellation);
            return;
        }
        var partition = EdgeQueueKeys.PartitionFor(connection.TenantId!, connection.GameId!,
            connection.RulesVersion, join.QueueType);
        try
        {
            var reply = await queueClient!.EnqueueAsync(connection.TenantId!, connection.GameId!,
                connection.BindingId!, connection.SessionId!, partition, join.OperationId, cancellation);
            var ticket = reply.Ticket;
            await connection.SendJsonAsync(new EdgeMatchStatus
            {
                TicketId = ticket.TicketId,
                State = EdgeMatchStates.FromStore(ticket.State),
                ReservationId = ticket.ReservationId is { Length: > 0 } id ? id : null,
                RoomId = null
            }, cancellation);
            // From here the routing watch owns the push: when the mount's room appears the
            // round started, and the ticket's commit turns it into the match notice.
            routingMonitor?.Subscribe(connection.StreamerId!, connection.ConnectionId,
                room => OnRoomRoutedAsync(connection, room));
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, join.OperationId, cancellation, queueErrors: true);
        }
    }

    private async Task HandleCancelQueueAsync(EdgeConnection connection, JsonElement root,
        CancellationToken cancellation)
    {
        var cancel = root.Deserialize<EdgeCancelQueue>(EdgeJson.Options) ?? throw new JsonException();
        if (!connection.HasGameplay)
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "Start gameplay before changing a queue ticket.",
                OperationId = cancel.OperationId
            }, cancellation);
            return;
        }
        try
        {
            // The mount's ticket is the unit — the ticket id and version the client holds are
            // diagnostics, not the addressing (contracts 3.1 CancelQueue).
            var reply = await queueClient!.CancelAsync(connection.TenantId!, connection.GameId!,
                connection.BindingId!, cancel.OperationId, cancellation);
            var ticket = reply.Ticket;
            await connection.SendJsonAsync(new EdgeMatchStatus
            {
                TicketId = ticket.TicketId,
                State = EdgeMatchStates.FromStore(ticket.State),
                ReservationId = ticket.ReservationId is { Length: > 0 } id ? id : null,
                RoomId = null
            }, cancellation);
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, cancel.OperationId, cancellation, queueErrors: true);
        }
    }

    /// <summary>GameCommand (contracts 3.1): the store's transaction is the judge — the
    /// current session, the active participation, the live epoch. The Accepted receipt means
    /// durably in the round's input record; the Applied word arrives on the sync stream with
    /// the delta that consumes the command, which is why the command is tracked with the
    /// fan-out before the store is asked.</summary>
    private async Task HandleGameCommandAsync(EdgeConnection connection, EdgeGameCommand command,
        CancellationToken cancellation)
    {
        if (inputClient is null || !connection.HasGameplay || connection.TenantId is not { Length: > 0 })
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "Start gameplay before sending game commands.",
                OperationId = command.CommandId
            }, cancellation);
            return;
        }
        if (command.RoomId is not { Length: > 0 } || command.RoomEpoch <= 0
            || command.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "A command must name its room, epoch and payload.",
                OperationId = command.CommandId
            }, cancellation);
            return;
        }
        // Registered first: a frame that consumes the command the instant the receipt returns
        // still finds its owner. A command the store then refuses is untracked below.
        fanout?.TrackCommand(command.RoomId, command.CommandId, connection.ConnectionId);
        try
        {
            var reply = await inputClient.AcceptClientInputAsync(connection.TenantId!,
                connection.SessionId!, command.RoomId, command.RoomEpoch, connection.GameId!,
                command.CommandId, command.Payload.GetRawText(), cancellation);
            await connection.SendJsonAsync(new EdgeCommandReceipt
            {
                CommandId = reply.CommandId,
                Status = "Accepted",
                ReceiptId = $"{reply.RoomId}:{reply.Sequence}"
            }, cancellation);
        }
        catch (RpcException error)
        {
            fanout?.UntrackCommand(command.RoomId, command.CommandId);
            await RefuseAsync(connection, error, command.CommandId, cancellation, queueErrors: true,
                invalidText: "The command is not valid for the mount's current round.");
        }
    }

    /// <summary>LeaveRound (contracts 3.1): the participation withdraws on the store's word and
    /// the room runs to its natural settlement for everyone else. The seat leaves the fan-out —
    /// no more frames for a room the mount stepped out of — and success is silent: the protocol
    /// has no leave acknowledgment, and the leaver's next word from the plane is a fresh match.</summary>
    private async Task HandleLeaveRoundAsync(EdgeConnection connection, EdgeLeaveRound leave,
        CancellationToken cancellation)
    {
        if (inputClient is null || !connection.HasGameplay || connection.TenantId is not { Length: > 0 })
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "Start gameplay before leaving a round.",
                OperationId = leave.OperationId
            }, cancellation);
            return;
        }
        if (leave.RoomId is not { Length: > 0 })
        {
            await connection.SendJsonAsync(new EdgeError
            {
                Code = EdgeErrorCodes.InvalidMessage,
                Message = "A leave must name its room.",
                OperationId = leave.OperationId
            }, cancellation);
            return;
        }
        try
        {
            await inputClient.WithdrawParticipationAsync(connection.TenantId!, connection.SessionId!,
                leave.RoomId, connection.GameId!, leave.OperationId, cancellation);
            fanout?.Leave(connection.ConnectionId);
        }
        catch (RpcException error)
        {
            await RefuseAsync(connection, error, leave.OperationId, cancellation, queueErrors: true,
                invalidText: "The room is not active for this mount.");
        }
    }

    /// <summary>The routing watch callback: the mount's room appeared (or moved). The room
    /// becomes routing-visible the moment the round starts — before the ticket commits — so the
    /// push converges on the ticket: while the reservation is mid-flight (Reserved/Preparing)
    /// it keeps reading, and a committed ticket is the match the client has not been told about
    /// yet, pushed once per room. A ticket that left the round (requeued, cancelled, expired)
    /// ends the watch silently: the room belongs to a round this mount is no longer in, and a
    /// later room re-triggers this callback.</summary>
    private async Task OnRoomRoutedAsync(EdgeConnection connection, string room)
    {
        if (room.Length == 0 || !connection.TryClaimRoomNotice(room)) return;
        for (var deadline = Environment.TickCount64 + 15_000;
             Environment.TickCount64 < deadline;)
        {
            TicketProto? ticket;
            try
            {
                ticket = await queueClient!.GetTicketAsync(connection.TenantId!, connection.GameId!,
                    connection.BindingId!, CancellationToken.None);
            }
            catch (RpcException)
            {
                // Best-effort push: a transient plane failure is retried inside the window, and
                // the room fact stays durable — the client can also learn it by reconnecting.
                await Task.Delay(150, CancellationToken.None);
                continue;
            }
            if (ticket is not ({ State: "Reserved" or "Preparing" or "Committed" })) return;
            if (ticket.State == "Committed")
            {
                await connection.SendJsonAsync(new EdgeMatchStatus
                {
                    TicketId = ticket.TicketId,
                    State = EdgeMatchStates.Matched,
                    ReservationId = ticket.ReservationId is { Length: > 0 } id ? id : null,
                    RoomId = room
                }, CancellationToken.None);
                await JoinRoomFanoutAsync(connection, room);
                return;
            }
            // The round pipeline is still driving this reservation: start → prepare → commit
            // spans several plane round-trips after the room already routes.
            await Task.Delay(150, CancellationToken.None);
        }
    }

    /// <summary>A resume re-enters the room the routed record names — the server's
    /// participation record decides, never the client's diagnostics. A fresh connection
    /// carries no mount facts, so the binding status restores them first; the routed room
    /// supplies the tenant the resume itself cannot name.</summary>
    private async Task TryRejoinRoomAsync(EdgeConnection connection, string streamerId)
    {
        if (fanout is null || roundClient is null || routingMonitor is null) return;
        var room = routingMonitor.RoomFor(streamerId);
        if (room is null) return;
        if (!connection.HasGameplay && connection.AccessCredential is { Length: > 0 })
        {
            var route = routingMonitor.RoomRouteFor(room);
            if (route is not { TenantId: { Length: > 0 } }) return;
            try
            {
                var status = await sessionClient.GameplayStatusAsync(route.TenantId,
                    connection.AccessCredential!, CancellationToken.None);
                if (status.BindingId is not { Length: > 0 }) return;
                // The mount facts of the round the resume lands in — including the tenant
                // every later uplink on this connection names again.
                connection.MarkTenant(route.TenantId);
                connection.MarkGameplay(status.BindingId, status.GameId, null);
            }
            catch (RpcException)
            {
                // No mount on record for this session: there is nothing to re-enter.
                return;
            }
        }
        await JoinRoomFanoutAsync(connection, room);
    }

    /// <summary>Seats the connection in its match's room: the routing snapshot names the
    /// owning host, the round store names the epoch and the side this binding plays, and the
    /// fan-out owns the stream from there. Best-effort within a convergence window — the
    /// facts can lag the commit by a routing beat; past the window the client still learns
    /// the room from its matchStatus and recovers by reconnecting.</summary>
    private async Task JoinRoomFanoutAsync(EdgeConnection connection, string roomId)
    {
        if (fanout is null || roundClient is null) return;
        for (var deadline = Environment.TickCount64 + 15_000;
             Environment.TickCount64 < deadline;)
        {
            var route = routingMonitor?.RoomRouteFor(roomId);
            if (route is null || route.Endpoint.Length == 0)
            {
                // The owning host has not reported its endpoint yet — the next beat carries it.
                await Task.Delay(200, CancellationToken.None);
                continue;
            }
            RoundStatusReply? round;
            try
            {
                round = await roundClient.GetRoundAsync(route.TenantId, roomId, route.GameId,
                    CancellationToken.None);
            }
            catch (RpcException)
            {
                await Task.Delay(200, CancellationToken.None);
                continue;
            }
            if (round is null)
            {
                await Task.Delay(200, CancellationToken.None);
                continue;
            }
            var participation = round.Participants.FirstOrDefault(
                participant => participant.BindingId == connection.BindingId);
            if (participation is null) return;
            try
            {
                fanout.Join(roomId, route.GameId, route.Endpoint, round.Epoch,
                    participation.Role, connection);
            }
            catch
            {
                // The subscription could not open; the room's stream is not the connection's
                // to hear. The client's requestSnapshot after a reconnect re-seats it.
            }
            return;
        }
    }

    private static async Task RefuseAsync(EdgeConnection connection, RpcException error,
        string? operationId, CancellationToken cancellation, bool queueErrors = false,
        string? invalidText = null)
    {
        var mapped = queueErrors
            ? EdgeErrorMapping.FromQueueRpcStatus(error.StatusCode)
            : EdgeErrorMapping.FromRpcStatus(error.StatusCode);
        // The refusal message stays generic: no platform detail, no tenant, no credential
        // material — the code is the contract, the text is for a human.
        await connection.SendJsonAsync(new EdgeError
        {
            Code = mapped,
            Message = mapped switch
            {
                EdgeErrorCodes.SessionRevoked => "The session was refused or has ended.",
                EdgeErrorCodes.SessionReplaced => "The session was replaced by another login.",
                EdgeErrorCodes.InvalidMessage => invalidText ?? (queueErrors
                    ? "The queue request is not valid for the mount's current ticket."
                    : "The request is not valid."),
                _ => "The request could not be completed."
            },
            OperationId = operationId
        }, cancellation);
    }

    /// <summary>Accumulates one text frame, enforcing the total size cap. Returns null on a
    /// client-initiated close.</summary>
    private async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellation)
    {
        var buffer = new byte[ReceiveChunkBytes];
        using var frame = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            if (received.MessageType == WebSocketMessageType.Close) return null;
            frame.Write(buffer, 0, received.Count);
            if (frame.Length > options.MaxMessageBytes) throw new EdgeMessageTooLargeException();
            if (received.EndOfMessage)
            {
                if (frame.Length == 0) throw new JsonException();
                return Encoding.UTF8.GetString(frame.ToArray());
            }
        }
    }

    private static Task SendFrameAsync(WebSocket socket, string frame, CancellationToken cancellation)
        => socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, cancellation);

    private static Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
        => socket.CloseAsync(status, reason, CancellationToken.None);

    /// <summary>Thrown when a frame exceeds the transport cap; the connection closes with
    /// MessageTooLarge — a client that oversized one frame may size the next worse.</summary>
    private sealed class EdgeMessageTooLargeException : Exception;
}
