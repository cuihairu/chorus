using Google.Api;
using Google.Protobuf.Reflection;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The FIX-WEB-01 browser Admin API contract, pinned against the compiled descriptors rather
/// than any hand-maintained list. The browser console (FIX-WEB-02 onward) speaks HTTP+JSON at
/// /admin/api/v1/&lt;kebab-of-rpc-name&gt; with the request message as the body; the route table,
/// the audited-command shape and the operator identity surface are read here straight from
/// m3.proto's annotations, so the contract cannot drift from the proto. The enforcement of the
/// role table against live hosts is the FIX-WEB-03 acceptance line — this file pins what the
/// contract says, not yet what the server does.
/// </summary>
public sealed class RedesignWeb01AdminContractTests
{
    private static ServiceDescriptor AdminService()
    {
        var service = Chorus.Rpc.M3Reflection.Descriptor.Services
            .Single(s => s.FullName == "chorus.m3.Admin");
        return service;
    }

    // The route rule itself, forward direction: a route is /admin/api/v1/ + kebab-case of the
    // rpc name, derived by word boundary — never a hand-written alias. The route table below
    // is checked against this derivation, so the browser client can compute every path from
    // the descriptor with no exceptions.
    private static string KebabOf(string rpcName) => string.Concat(rpcName.Select((c, i) =>
        i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    private static (MethodDescriptor Method, HttpRule Rule)? RuleOf(ServiceDescriptor admin, string path)
    {
        var method = admin.Methods
            .SingleOrDefault(m => "/admin/api/v1/" + KebabOf(m.Name) == path);
        if (method is null)
        {
            return null;
        }

        var options = method.GetOptions();
        return options is not null && options.HasExtension(AnnotationsExtensions.Http)
            ? (method, options.GetExtension(AnnotationsExtensions.Http))
            : null;
    }

    private static (string Path, string Kind, string MinRole)[] ContractRoutes() =>
    [
        ("/admin/api/v1/who-am-i", "read", "viewer"),
        ("/admin/api/v1/get-streamer-snapshot", "read", "viewer"),
        ("/admin/api/v1/get-room-snapshot", "read", "viewer"),
        ("/admin/api/v1/trace-round", "read", "viewer"),
        ("/admin/api/v1/get-room-epoch", "read", "viewer"),
        ("/admin/api/v1/get-operation-status", "read", "viewer"),
        ("/admin/api/v1/get-leaderboard-top", "read", "viewer"),
        ("/admin/api/v1/get-leaderboard-rank", "read", "viewer"),
        ("/admin/api/v1/get-leaderboard-around", "read", "viewer"),
        ("/admin/api/v1/adjust-leaderboard-entry", "command", "gm"),
        ("/admin/api/v1/reset-leaderboard-entry", "command", "gm"),
        ("/admin/api/v1/rebuild-leaderboard", "command", "gm"),
        ("/admin/api/v1/suspend-match-queue", "command", "gm"),
        ("/admin/api/v1/resume-match-queue", "command", "gm"),
        ("/admin/api/v1/retry-room-recovery", "command", "gm"),
        ("/admin/api/v1/drain-process", "command", "gm"),
        ("/admin/api/v1/retry-settlement", "command", "gm"),
        ("/admin/api/v1/reconcile-platform-task", "command", "gm"),
        ("/admin/api/v1/bump-room-epoch", "command", "gm"),
        ("/admin/api/v1/dispatch-room-command", "command", "gm"),
    ];

    [Fact]
    public void TheBrowserRouteTableMirrorsTheAdminSurface()
    {
        var admin = AdminService();
        var routes = ContractRoutes();

        // Every contract route exists on the Admin surface, bound as a whole-body POST.
        foreach (var (path, kind, role) in routes)
        {
            var entry = RuleOf(admin, path);
            Assert.NotNull(entry);
            Assert.Equal(path, entry!.Value.Rule.Post);
            Assert.Equal("*", entry.Value.Rule.Body);
            Assert.True(kind is "read" or "command");
            Assert.True(role is "viewer" or "gm");
        }

        // The table covers every annotated entry exactly once — no route outside the table.
        var annotated = admin.Methods
            .Where(m => m.GetOptions()?.HasExtension(AnnotationsExtensions.Http) == true)
            .Select(m => m.GetOptions()!.GetExtension(AnnotationsExtensions.Http).Post)
            .ToHashSet();
        Assert.Equal(routes.Select(r => r.Path).ToHashSet(), annotated);

        // Ready is deliberately unannotated: it is a process-cert health word, and the
        // browser's health word is who-am-i. Exactly the non-Ready surface carries routes.
        Assert.Equal(21, admin.Methods.Count);
        Assert.Equal(20, admin.Methods.Count(m => m.GetOptions()?.HasExtension(AnnotationsExtensions.Http) == true));
        Assert.Null(admin.Methods.Single(m => m.Name == "Ready").GetOptions());
    }

    [Fact]
    public void EveryCommandRouteDeclaresTheAuditedTriple()
    {
        // Architecture 12.1: every command carries RequestId, operator and reason — the
        // fields are declared at positions 1, 2, 3 so no command can grow an identity
        // field ahead of them and the browser client can fill them positionally.
        var admin = AdminService();
        foreach (var (path, kind, _) in ContractRoutes().Where(r => r.Kind == "command"))
        {
            var request = RuleOf(admin, path)!.Value.Method.InputType;
            Assert.Equal("request_id", request.FindFieldByNumber(1).Name);
            Assert.Equal("operator", request.FindFieldByNumber(2).Name);
            Assert.Equal("reason", request.FindFieldByNumber(3).Name);
        }
    }

    [Fact]
    public void WhoAmICarriesTheOperatorIdentitySurface()
    {
        var admin = AdminService();
        var reply = admin.Methods.Single(m => m.Name == "WhoAmI").OutputType;
        Assert.Equal("operator_id", reply.FindFieldByNumber(1)?.Name);
        Assert.Equal("role", reply.FindFieldByNumber(2)?.Name);
        Assert.Equal("tenants", reply.FindFieldByNumber(3)?.Name);
        Assert.Equal("games", reply.FindFieldByNumber(4)?.Name);
        Assert.Equal("enabled", reply.FindFieldByNumber(5)?.Name);
        // Scope semantics stay the FIX-GM-06 store's: empty array = unbounded.
    }
}
