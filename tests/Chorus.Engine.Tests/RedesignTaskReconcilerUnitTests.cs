using Chorus.Platform;
using Chorus.Rpc;
using Grpc.Core;
using Xunit;
using static Chorus.Rpc.PlatformTask;

namespace Chorus.Engine.Tests;

/// <summary>
/// FIX-END-09's executor-side gate in a unit pin: a task bound to a standing end operation
/// is listed like any due work, but until the operation's ladder authorizes platform work
/// the executor takes no lease, writes no attempt and issues no call — the skip is the whole
/// protocol action. Authorization turns the very next pass into the task's first call, and
/// an ungated launch task never asks the question at all.
/// </summary>
public sealed class RedesignTaskReconcilerUnitTests
{
    [Fact]
    public async Task AnUnauthorizedEndTaskIsSkippedWithoutAnyLeaseAndRunsWhenAuthorized()
    {
        var tasks = new ScriptedTasks(
            new TaskStateReply
            {
                TaskId = "task-end-1", BindingId = "binding-1", OperationId = "end-op-1",
                Status = "Pending", DesiredJson = """{"kind":"end-round"}""",
                BarrierEndOperationId = "end-op-1"
            },
            new TaskStateReply
            {
                TaskId = "task-screen", BindingId = "binding-1", OperationId = "op-screen",
                Status = "Pending", DesiredJson = """{"kind":"screen-link"}"""
            });
        var adapter = new RecordingAdapter();
        using var cancellation = new CancellationTokenSource();
        using var reconciler = new Chorus.PlatformGateway.TaskReconciler(tasks, adapter,
            "gateway-unit", intervalMs: 20, leaseSeconds: 30, deadlineSeconds: 30,
            callTimeoutSeconds: 5);
        await reconciler.StartAsync(cancellation.Token);

        // The barrier answers not-authorized: the gated task gets no protocol action — no
        // lease, no attempt, no call — while the ungated launch task beside it runs to
        // completion untouched by the question.
        tasks.BarrierAuthorized = false;
        var launchDone = await WaitAsync(() => adapter.Calls.Contains("op-screen"), 5_000);
        Assert.True(launchDone, "The launch task never ran.");
        await Task.Delay(300);
        Assert.DoesNotContain("end-op-1", adapter.Calls);
        Assert.DoesNotContain("task-end-1", tasks.Leased);
        Assert.DoesNotContain("task-end-1", tasks.Attempted);

        // The ladder reaches PlatformTaskAuthorized: the next pass asks, hears yes, and the
        // end task's first call leaves exactly once.
        tasks.BarrierAuthorized = true;
        var endDone = await WaitAsync(() => adapter.Calls.Contains("end-op-1"), 5_000);
        Assert.True(endDone, "The authorized end task never ran.");
        Assert.Contains("task-end-1", tasks.Leased);
        Assert.Contains("task-end-1", tasks.Attempted);
        Assert.Contains("task-end-1", tasks.Completed);
        await cancellation.CancelAsync();
        await reconciler.StopAsync(CancellationToken.None);
    }

    private static async Task<bool> WaitAsync(Func<bool> probe, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (probe()) return true;
            await Task.Delay(20);
        }
        return probe();
    }

    /// <summary>FIX-END-10's capability answer in a unit pin: the platform requires no call
    /// for this operation, and the ground it names is exactly what the terminal record
    /// keeps — a NotRequired close, never an executed one.</summary>
    [Fact]
    public async Task ANotRequiredAnswerClosesTheTaskWithItsCapabilityBasis()
    {
        var tasks = new ScriptedTasks(new TaskStateReply
        {
            TaskId = "task-end-nr", BindingId = "binding-1", OperationId = "end-op-nr",
            Status = "Pending", DesiredJson = """{"kind":"end-round"}""",
            BarrierEndOperationId = "end-op-nr"
        });
        tasks.BarrierAuthorized = true;
        var adapter = new RecordingAdapter { NotRequiredNext = true };
        using var cancellation = new CancellationTokenSource();
        using var reconciler = new Chorus.PlatformGateway.TaskReconciler(tasks, adapter,
            "gateway-unit", intervalMs: 20, leaseSeconds: 30, deadlineSeconds: 30,
            callTimeoutSeconds: 5);
        await reconciler.StartAsync(cancellation.Token);

        var closed = await WaitAsync(() => tasks.NotRequiredCompleted.Contains("task-end-nr"), 5_000);
        Assert.True(closed, "The not-required answer never closed the task.");
        var basis = tasks.NotRequiredBasis["task-end-nr"];
        Assert.Contains("no call required", basis);
        Assert.DoesNotContain("task-end-nr", tasks.Completed);
        await cancellation.CancelAsync();
        await reconciler.StopAsync(CancellationToken.None);
    }

    /// <summary>FIX-END-10's escalation in a unit pin: an outcome the platform cannot answer
    /// for, on a platform whose re-issue cannot be trusted, reaches NeedsAttention — while
    /// the same unanswerable outcome on an idempotent platform keeps asking, because its
    /// next question is always safe.</summary>
    [Fact]
    public async Task AnUnanswerableUnknownEscalatesOnlyWhenReissueIsUnsafe()
    {
        var parked = new TaskStateReply
        {
            TaskId = "task-end-unknown", BindingId = "binding-1", OperationId = "end-op-unknown",
            Status = "OutcomeUnknown", DesiredJson = """{"kind":"end-round"}""",
            AttemptDeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()
        };
        var tasks = new ScriptedTasks(parked);
        var adapter = new RecordingAdapter { QueryFaults = true, IdempotentReissue = false };
        using var cancellation = new CancellationTokenSource();
        using var reconciler = new Chorus.PlatformGateway.TaskReconciler(tasks, adapter,
            "gateway-unit", intervalMs: 20, leaseSeconds: 30, deadlineSeconds: 30,
            callTimeoutSeconds: 1);
        await reconciler.StartAsync(cancellation.Token);

        var escalated = await WaitAsync(() => tasks.NeedsAttention.Contains("task-end-unknown"), 5_000);
        Assert.True(escalated, "The unanswerable unknown never reached NeedsAttention.");
        Assert.Contains("not idempotent", tasks.NeedsAttentionErrors["task-end-unknown"]);
        Assert.Empty(tasks.Resolved);
        await cancellation.CancelAsync();
        await reconciler.StopAsync(CancellationToken.None);

        // The same shape on a platform whose re-issue is safe: no escalation, the loop
        // keeps asking — the record, not patience, will answer it.
        var asking = new ScriptedTasks(new TaskStateReply
        {
            TaskId = "task-end-unknown-2", BindingId = "binding-1", OperationId = "end-op-unknown-2",
            Status = "OutcomeUnknown", DesiredJson = """{"kind":"end-round"}""",
            AttemptDeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds()
        });
        var patientAdapter = new RecordingAdapter { QueryFaults = true, IdempotentReissue = true };
        using var secondCancellation = new CancellationTokenSource();
        using var second = new Chorus.PlatformGateway.TaskReconciler(asking, patientAdapter,
            "gateway-unit", intervalMs: 20, leaseSeconds: 30, deadlineSeconds: 30,
            callTimeoutSeconds: 1);
        await second.StartAsync(secondCancellation.Token);
        await Task.Delay(500);
        Assert.Empty(asking.NeedsAttention);
        Assert.NotEmpty(patientAdapter.Queries);
        await secondCancellation.CancelAsync();
        await second.StopAsync(CancellationToken.None);
    }

    /// <summary>The external platform as a recording: every call executes and is named by
    /// its operation id, with FIX-END-10's two scriptable answers — a capability ground
    /// (NotRequired) and a query leg that cannot answer.</summary>
    private sealed class RecordingAdapter : IPlatformTaskAdapter
    {
        private readonly object _gate = new();
        public List<string> Calls { get; } = [];
        public List<string> Queries { get; } = [];
        public bool NotRequiredNext;
        public bool QueryFaults;
        public bool IdempotentReissue { get; set; } = true;

        public string Platform => "simulator";
        public bool ProductionReady => false;

        public Task<PlatformTaskOutcome> CallAsync(PlatformTaskCall call,
            CancellationToken cancellationToken = default)
        {
            PlatformTaskOutcome outcome;
            lock (_gate)
            {
                Calls.Add(call.OperationId);
                outcome = NotRequiredNext
                    ? new PlatformTaskOutcome(Executed: false,
                        """{"basis":"no call required on this platform"}""", NotRequired: true)
                    : new PlatformTaskOutcome(Executed: true,
                        """{"executed":true,"applied":true}""");
            }
            return Task.FromResult(outcome);
        }

        public Task<PlatformTaskOutcome> QueryAsync(string operationId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate) Queries.Add(operationId);
            if (QueryFaults)
                throw new HttpRequestException("The platform cannot answer for the operation.");
            return Task.FromResult(new PlatformTaskOutcome(Executed: false, null));
        }
    }

    /// <summary>The task face as a script: the listed tasks answer every listing until their
    /// branch completes them, the barrier answers one scripted authorization, and every
    /// protocol step is recorded so a step that should never happen is visible by name.</summary>
    private sealed class ScriptedTasks(params TaskStateReply[] listed) : PlatformTaskClient
    {
        public readonly object Gate = new();
        public readonly List<string> Leased = [];
        public readonly List<string> Attempted = [];
        public readonly List<string> Completed = [];
        public readonly List<string> NotRequiredCompleted = [];
        public readonly Dictionary<string, string> NotRequiredBasis = new(StringComparer.Ordinal);
        public readonly List<string> NeedsAttention = [];
        public readonly Dictionary<string, string> NeedsAttentionErrors = new(StringComparer.Ordinal);
        public readonly List<string> Resolved = [];
        public bool BarrierAuthorized;

        public ScriptedTasks() : this(Array.Empty<TaskStateReply>())
        {
        }

        public override AsyncUnaryCall<TaskListReply> ListReadyTasksAsync(TaskListRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            TaskListReply reply;
            lock (Gate)
            {
                reply = new TaskListReply();
                foreach (var task in listed)
                    if (!Completed.Contains(task.TaskId) && !NotRequiredCompleted.Contains(task.TaskId)
                        && !NeedsAttention.Contains(task.TaskId))
                        reply.Tasks.Add(task);
            }
            return Reply(reply);
        }

        public override AsyncUnaryCall<TaskBarrierReply> GetBarrierAuthorizationAsync(
            TaskBarrierRequest request, Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            bool authorized;
            lock (Gate) authorized = BarrierAuthorized;
            return Reply(new TaskBarrierReply
            {
                Authorized = authorized,
                Phase = authorized ? "PlatformTaskAuthorized" : "StopDeliveryIssued"
            });
        }

        public override AsyncUnaryCall<TaskStateReply> AcquireLeaseAsync(TaskLeaseRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Leased.Add(request.TaskId);
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "Pending" });
        }

        public override AsyncUnaryCall<TaskStateReply> BeginAttemptAsync(TaskAttemptRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Attempted.Add(request.TaskId);
            return Reply(new TaskStateReply
            {
                TaskId = request.TaskId, Status = "Executing",
                AttemptDeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeMilliseconds()
            });
        }

        public override AsyncUnaryCall<TaskStateReply> CompleteAttemptAsync(TaskCompleteRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Completed.Add(request.TaskId);
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "Completed" });
        }

        public override AsyncUnaryCall<TaskStateReply> CompleteNotRequiredAsync(TaskCompleteRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                NotRequiredCompleted.Add(request.TaskId);
                NotRequiredBasis[request.TaskId] = request.ObservedJson;
            }
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "NotRequired" });
        }

        public override AsyncUnaryCall<TaskStateReply> MarkNeedsAttentionAsync(TaskStopRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate)
            {
                NeedsAttention.Add(request.TaskId);
                NeedsAttentionErrors[request.TaskId] = request.LastError;
            }
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "NeedsAttention" });
        }

        public override AsyncUnaryCall<TaskStateReply> ResolveOutcomeAsync(TaskResolveRequest request,
            Metadata? headers = null, DateTime? deadline = null,
            CancellationToken cancellationToken = default)
        {
            lock (Gate) Resolved.Add(request.TaskId);
            return Reply(new TaskStateReply { TaskId = request.TaskId, Status = "Completed" });
        }

        private static AsyncUnaryCall<T> Reply<T>(T value) => new(Task.FromResult(value),
            Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
            () => new Metadata(), () => { });
    }
}
