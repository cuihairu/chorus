using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Control;
using Chorus.ControlHost;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var key = builder.Configuration["Control:Key"] ?? "";
if (key.Length < 24) throw new InvalidOperationException("Configure Control:Key with at least 24 characters.");
var users = builder.Configuration.GetSection("Admin:Users").Get<AdminUser[]>() ?? [];
if (users.Length == 0 || users.Any(u => string.IsNullOrWhiteSpace(u.Name) || string.IsNullOrWhiteSpace(u.PasswordHash) || u.Role is not ("viewer" or "operator" or "admin")))
    throw new InvalidOperationException("Configure Admin:Users with names, PasswordHasher hashes and viewer/operator/admin roles.");
builder.Services.AddSingleton(new ControlDatabase(builder.Configuration["Control:Database"] ?? "data/control.db"));
builder.Services.AddSingleton<RoomControlStore>();
builder.Services.AddHostedService<ControlMaintenance>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "chorus-admin"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("operate", p => p.RequireRole("operator", "admin"));
    options.AddPolicy("configure", p => p.RequireRole("admin"));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; object-src 'none'; frame-ancestors 'none'";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-store";
    if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers.Origin.Count > 0
        && context.Request.Headers.Origin != $"{context.Request.Scheme}://{context.Request.Host}") { context.Response.StatusCode = 403; return; }
    try { await next(); }
    catch (ControlConflictException ex) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (KeyNotFoundException ex) { context.Response.StatusCode = 404; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
    catch (ArgumentException ex) { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { error = ex.Message }); }
});
app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization(); app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { ok = true }));
app.MapPost("/api/login", async (Login request, HttpContext context) =>
{
    var user = users.FirstOrDefault(u => u.Name == request.Name);
    var hasher = new PasswordHasher<string>();
    // Hash a dummy value for unknown accounts too; credential contents never enter audit/logs.
    var hash = user?.PasswordHash ?? users[0].PasswordHash;
    var valid = hasher.VerifyHashedPassword(request.Name ?? "", hash, request.Password ?? "") != PasswordVerificationResult.Failed;
    if (!valid || user is null) return Results.Unauthorized();
    await context.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role)], CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok(new { user.Name, user.Role });
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
app.MapGet("/api/me", (HttpContext c) => new { name = c.User.Identity!.Name, role = c.User.FindFirstValue(ClaimTypes.Role) }).RequireAuthorization();
var internalApi = app.MapGroup("/internal").AddEndpointFilter(async (context, next) =>
{
    var supplied = context.HttpContext.Request.Headers["X-Control-Key"].ToString();
    return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(key)))
        ? await next(context) : Results.Unauthorized();
});
internalApi.MapPost("/nodes/report", (NodeReport report, RoomControlStore s) => s.ReportNode(report));
internalApi.MapGet("/nodes", (RoomControlStore s) => s.Nodes());
internalApi.MapGet("/rooms", (RoomControlStore s) => s.Rooms().Select(r => r with { Inbox = null, Receipts = null, Snapshot = null }));
internalApi.MapGet("/rooms/{id}", (string id, bool? checkpoint, RoomControlStore s) => s.Room(id) is { } r ? Results.Ok(r with { Inbox = null, Receipts = null, Snapshot = checkpoint == true ? r.Snapshot : null }) : Results.NotFound());
internalApi.MapPost("/rooms/assign", (RoomAssignment request, RoomControlStore s) => s.Assign(request));
internalApi.MapPost("/rooms/{id}/prepare", (string id, OwnerRequest owner, RoomControlStore s) => s.Prepare(id, owner) with { Snapshot = null, Receipts = null });
internalApi.MapPost("/rooms/{id}/ready", (string id, OwnerRequest owner, RoomControlStore s) => s.Ready(id, owner));
internalApi.MapPost("/rooms/{id}/commit", (string id, TickCommit commit, RoomControlStore s) => new { committed = s.Commit(id, commit) });
internalApi.MapPost("/events", (Chorus.Protocol.Models.AudienceEvent e, RoomControlStore s) => new { accepted = true, duplicate = !s.AppendEvent(e) });
internalApi.MapPost("/connections/connect", (ConnectionRecord c, RoomControlStore s) => { s.Connect(c); return Results.Ok(); });
internalApi.MapPost("/connections/disconnect", (ConnectionRecord c, RoomControlStore s) => { s.Disconnect(c); return Results.Ok(); });
internalApi.MapPost("/connections/renew", (ConnectionRenew r, RoomControlStore s) => { s.RenewConnections(r.GatewayId,r.InstanceId,r.Connections); return Results.Ok(); });
internalApi.MapGet("/connections", (RoomControlStore s) => s.Connections());
var admin = app.MapGroup("/api").RequireAuthorization();
admin.MapGet("/summary", (RoomControlStore s) => s.Summary());
admin.MapGet("/history", (RoomControlStore s) => s.History());
admin.MapGet("/nodes", (RoomControlStore s) => s.Nodes());
admin.MapGet("/rooms", (RoomControlStore s) => s.Rooms().Select(r => r with { Inbox = null, Receipts = null, Snapshot = null }));
admin.MapGet("/rooms/{id}", (string id, RoomControlStore s) => s.Room(id) is { } r ? Results.Ok(new { room = r with { Inbox = null, Receipts = null }, pendingEvents = r.Inbox?.Count ?? 0, connections = s.Connections().Where(c => c.SessionId == id) }) : Results.NotFound());
admin.MapGet("/connections", (RoomControlStore s) => s.Connections());
admin.MapGet("/streamers", (RoomControlStore s) => s.Streamers());
admin.MapGet("/queue", (RoomControlStore s) => s.Queue());
admin.MapGet("/matches", (RoomControlStore s) => s.Matches());
admin.MapGet("/rules", (RoomControlStore s) => s.Rules());
admin.MapGet("/boards", (RoomControlStore s) => s.Boards().Select(b => new { b.Id, b.Scope, b.Season, b.Unit, b.Frozen, b.UpdatedAtMs, count = b.Scores.Count }));
admin.MapGet("/boards/{id}", (string id, string? search, int? page, int? size, RoomControlStore s) =>
{
    var b = s.Boards().FirstOrDefault(b => b.Id == id);
    if (b is null) return Results.NotFound();
    long? previous = null; var rank = 0; var index = 0;
    var all = b.Scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p =>
    { index++; if (previous != p.Value) rank = index; previous = p.Value; return new { participantId = p.Key, score = p.Value, rank }; }).ToArray();
    var filtered = all.Where(p => string.IsNullOrWhiteSpace(search) || p.participantId.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    return Results.Ok(new { b.Id, b.Scope, b.Unit, b.Season, b.Frozen, b.UpdatedAtMs, total = filtered.Length, entries = filtered.Skip((Math.Max(1,page ?? 1)-1)*Math.Clamp(size ?? 50,1,200)).Take(Math.Clamp(size ?? 50,1,200)) });
});
admin.MapGet("/audit", (int? limit, RoomControlStore s) => s.Audits(limit ?? 100));
admin.MapPost("/nodes/{id}/drain", (string id, DrainRequest r, HttpContext c, RoomControlStore s) => s.Drain(id,r.Draining,c.User.Identity!.Name!)).RequireAuthorization("operate");
admin.MapPost("/rooms/{id}/action", (string id, RoomAction r, HttpContext c, RoomControlStore s) => s.Action(id,r,c.User.Identity!.Name!)).RequireAuthorization("operate");
admin.MapPost("/rooms/{id}/move", (string id, RoomMove r, HttpContext c, RoomControlStore s) => s.Move(id,r,c.User.Identity!.Name!)).RequireAuthorization("operate");
admin.MapPost("/rooms/{id}/abort", (string id, EpochRequest r, HttpContext c, RoomControlStore s) => s.Abort(id,r.Epoch,c.User.Identity!.Name!)).RequireAuthorization("operate");
admin.MapPost("/streamers", (StreamerProfile r, HttpContext c, RoomControlStore s) => s.SaveProfile(r,c.User.Identity!.Name!)).RequireAuthorization("configure");
admin.MapPost("/rules", (MatchRules r, HttpContext c, RoomControlStore s) => s.SetRules(r,c.User.Identity!.Name!)).RequireAuthorization("configure");
admin.MapPost("/streamers/{id}/join", (string id, MatchJoin r, HttpContext c, RoomControlStore s) => s.Join(id,r,c.User.Identity!.Name!)).RequireAuthorization("operate");
admin.MapPost("/streamers/{id}/leave", (string id, HttpContext c, RoomControlStore s) => new { removed = s.Leave(id,c.User.Identity!.Name!) }).RequireAuthorization("operate");
admin.MapPost("/matches/{id}/finish", (string id, MatchFinish r, HttpContext c, RoomControlStore s) => s.Finish(id,r,c.User.Identity!.Name!)).RequireAuthorization("operate");
admin.MapPost("/boards/{id}/score", (string id, BoardScoreRequest r, HttpContext c, RoomControlStore s) => s.ChangeScore(id,r.Scope,r.Season,r.Change,c.User.Identity!.Name!)).RequireAuthorization("configure");
admin.MapPost("/boards/{id}/freeze", (string id, HttpContext c, RoomControlStore s) => s.Freeze(id,c.User.Identity!.Name!)).RequireAuthorization("configure");
app.Run();

namespace Chorus.ControlHost
{
    public partial class Program;
    public sealed record AdminUser(string Name, string PasswordHash, string Role);
    public sealed record Login(string Name, string Password);
    public sealed record DrainRequest(bool Draining);
    public sealed record EpochRequest(long Epoch);
    public sealed record BoardScoreRequest(string Scope, string Season, ScoreChange Change);
    public sealed record ConnectionRenew(string GatewayId, string InstanceId, IReadOnlyList<ConnectionRecord> Connections);
}
