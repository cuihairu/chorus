using System.Net.Http;
using Chorus.Control;
using Chorus.Data;
using Chorus.Engine.Abstractions;
using Chorus.Engine.Routing;
using Chorus.Engine.Session;
using Chorus.Messaging.Ipc;
using Chorus.Protocol.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chorus.Engine.Tests;

public sealed class ControlHostFencingTests
{
    [Fact]
    public async Task DelayedLiveActorCommitIsRejectedAfterPostgresAuthorityHandoff()
    {
        using var testDatabase = new PostgresTestDatabase();
        const string key = "test-control-key-with-more-than-24-characters";
        var passwordHash = new PasswordHasher<string>().HashPassword("test-admin", "unused-test-password");
        using var factory = new WebApplicationFactory<global::Chorus.ControlHost.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Control:Key", key);
            builder.UseSetting("Control:Provider", "PostgreSql");
            builder.UseSetting("Control:ConnectionString", testDatabase.ConnectionString);
            builder.UseSetting("Admin:Users:0:Name", "test-admin");
            builder.UseSetting("Admin:Users:0:Role", "admin");
            builder.UseSetting("Admin:Users:0:PasswordHash", passwordHash);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Control:Key"] = key,
                ["Control:Provider"] = "PostgreSql",
                ["Control:ConnectionString"] = testDatabase.ConnectionString,
                ["Admin:Users:0:Name"] = "test-admin",
                ["Admin:Users:0:Role"] = "admin",
                ["Admin:Users:0:PasswordHash"] = passwordHash
            }));
        });

        var partition = new DelaySecondPrepareHandler(factory.Server.CreateHandler());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        { ["Control:Url"] = "http://localhost/", ["Control:Key"] = key }).Build();
        using var oldOwner = new ControlClient(config, "worker-a", partition);
        using var newOwner = new ControlClient(config, "worker-b", factory.Server.CreateHandler());
        var store = factory.Services.GetRequiredService<RoomControlStore>();
        store.ReportNode(Worker("worker-a", oldOwner.InstanceId));
        store.ReportNode(Worker("worker-b", newOwner.InstanceId));
        var room = store.Assign(new("pg-fenced-room-" + Guid.NewGuid().ToString("N")));
        using var game = new NoopGame();
        var actor = new SessionActor(room.Id, new SessionConfig { TickDurationMs = 50 }, game,
            durability: new ControlTickDurability(oldOwner, room), restoredTick: room.TickId);
        actor.Start();
        try
        {
            await WaitUntil(() => store.Room(room.Id)!.TickId >= 1);
            await partition.SecondPrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var moving = store.Move(room.Id, new("worker-b", room.Epoch), "test");
            var handedOff = store.Handoff(room.Id, moving.Epoch);
            store.Ready(room.Id, new(handedOff.WorkerId, handedOff.WorkerInstance, handedOff.Epoch));
            var tickAtHandoff = handedOff.TickId;
            partition.ReleaseSecondPrepare();

            await WaitUntil(() => actor.DurabilityFailed);
            Assert.Equal("worker-b", store.Room(room.Id)!.WorkerId);
            Assert.Equal(tickAtHandoff, store.Room(room.Id)!.TickId);
        }
        finally
        {
            partition.ReleaseSecondPrepare();
            await actor.DisposeAsync();
        }
    }

    [Fact]
    public async Task DelayedLiveActorCommitIsRejectedAfterHttpAuthorityHandoff()
    {
        var dir=Path.Combine(Path.GetTempPath(),"chorus-control-http-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var key="test-control-key-with-more-than-24-characters";
        using var factory=new WebApplicationFactory<global::Chorus.ControlHost.Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Control:Key",key);
            builder.UseSetting("Control:Database",Path.Combine(dir,"control.db"));
            builder.UseSetting("Admin:Users:0:Name","test-admin");
            builder.UseSetting("Admin:Users:0:Role","admin");
            builder.UseSetting("Admin:Users:0:PasswordHash",new PasswordHasher<string>().HashPassword("test-admin","unused-test-password"));
            builder.ConfigureAppConfiguration((_,configuration)=>configuration.AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Control:Key"]=key,
                ["Control:Database"]=Path.Combine(dir,"control.db"),
                ["Admin:Users:0:Name"]="test-admin",
                ["Admin:Users:0:Role"]="admin",
                ["Admin:Users:0:PasswordHash"]=new PasswordHasher<string>().HashPassword("test-admin","unused-test-password")
            }));
        });

        try
        {
            var serverHandler=factory.Server.CreateHandler();
            var partition=new DelaySecondPrepareHandler(serverHandler);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["Control:Url"]="http://localhost/",["Control:Key"]=key }).Build();
            using var oldOwner=new ControlClient(config,"worker-a",partition);
            using var newOwner=new ControlClient(config,"worker-b",factory.Server.CreateHandler());
            var store=factory.Services.GetRequiredService<RoomControlStore>();
            store.ReportNode(Worker("worker-a",oldOwner.InstanceId));
            store.ReportNode(Worker("worker-b",newOwner.InstanceId));
            var room=store.Assign(new("room"));
            using var game=new NoopGame();
            var actor=new SessionActor("room",new SessionConfig { TickDurationMs=50 },game,
                durability:new ControlTickDurability(oldOwner,room),restoredTick:room.TickId);
            actor.Start();
            try
            {
                await WaitUntil(()=>store.Room("room")!.TickId>=1);
                await partition.SecondPrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

                var moving=store.Move("room",new("worker-b",room.Epoch),"test");
                var handedOff=store.Handoff("room",moving.Epoch);
                store.Ready("room",new(handedOff.WorkerId,handedOff.WorkerInstance,handedOff.Epoch));
                var tickAtHandoff=handedOff.TickId;
                partition.ReleaseSecondPrepare();

                await WaitUntil(()=>actor.DurabilityFailed);
                Assert.Equal("worker-b",store.Room("room")!.WorkerId);
                Assert.Equal(tickAtHandoff,store.Room("room")!.TickId);
            }
            finally
            {
                partition.ReleaseSecondPrepare();
                await actor.DisposeAsync();
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir,true);
        }
    }

    private static NodeReport Worker(string id,string instance)=>new(id,instance,"machine-"+id,"worker",
        new IpcNetworkEndpoint("127.0.0.1",3000+(id=="worker-a"?1:2)),10);
    private static async Task WaitUntil(Func<bool> condition)
    {
        var until=DateTime.UtcNow+TimeSpan.FromSeconds(10);
        while(!condition()&&DateTime.UtcNow<until) await Task.Delay(5);
        Assert.True(condition(),"Condition did not become true before timeout.");
    }
    private sealed class DelaySecondPrepareHandler(HttpMessageHandler inner):DelegatingHandler(inner)
    {
        private int _prepares;
        private readonly TaskCompletionSource _entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondPrepareEntered=>_entered;
        public void ReleaseSecondPrepare()=>_release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri?.AbsolutePath.EndsWith("/prepare",StringComparison.Ordinal)==true
                && Interlocked.Increment(ref _prepares)==2)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }
            return await base.SendAsync(request,ct);
        }
    }
    private sealed class NoopGame:IGameModule
    {
        public string Name=>"control-host-fencing";
        public object GetStateSnapshot()=>new { ok=true };
        public void ApplyEvents(long tickId,IReadOnlyList<AudienceEvent> events) { }
        public void RestoreState(long version,System.Text.Json.JsonElement state) { }
        public void Dispose() { }
    }
}
