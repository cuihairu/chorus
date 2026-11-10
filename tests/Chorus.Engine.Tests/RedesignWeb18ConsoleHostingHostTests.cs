using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chorus.ControlServer;
using Xunit;

namespace Chorus.Engine.Tests;

/// <summary>
/// The console's production hosting (FIX-WEB-18): when CHORUS_CONSOLE_DIST names a built
/// SPA directory, the browser endpoint serves its static files and falls back to index.html
/// for the client-side routes, while the Admin API keeps its own honest answers — an
/// unknown admin path is a plain 404 (never the SPA shell) and the operator bearer gate is
/// untouched. Without the variable the browser face stays API-only, exactly as before.
/// </summary>
public sealed class RedesignWeb18ConsoleHostingHostTests
{
    [Fact]
    public async Task TheBrowserEndpointServesTheConsoleShellAndKeepsTheApiHonest()
    {
        // A stand-in for the console's dist/: one shell page and one hashed asset, the
        // shapes vite emits. The host serves whatever the variable names — the fixture
        // keeps this test closed without a pnpm build.
        var dist = Path.Combine(Path.GetTempPath(), "chorus-web18-dist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dist, "assets"));
        var shell = """<!doctype html><html><body><div id="root">chorus-web18-shell</div><script src="/assets/web18-app.js"></script></body></html>""";
        await File.WriteAllTextAsync(Path.Combine(dist, "index.html"), shell);
        var asset = "console.log('web18 fixture');";
        await File.WriteAllTextAsync(Path.Combine(dist, "assets", "web18-app.js"), asset);
        try
        {
            await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
            await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
                new Dictionary<string, string>
                {
                    ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}",
                    ["CHORUS_CONSOLE_DIST"] = dist
                });
            await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

            var operators = new RedesignOperatorStore(environment.DatabaseConnectionString);
            await operators.RegisterOperatorAsync("gm-alice", "gm", [], [], "alice-token");

            using var client = BrowserClient(environment);
            var baseUrl = $"https://localhost:{environment.BrowserPort}";

            // (1) The root serves the shell page.
            using (var root = await client.GetAsync(baseUrl + "/"))
            {
                Assert.Equal(HttpStatusCode.OK, root.StatusCode);
                Assert.Contains("text/html", root.Content.Headers.ContentType?.ToString());
                Assert.Contains("chorus-web18-shell", await root.Content.ReadAsStringAsync());
            }

            // (2) A client-side route falls back to the same shell — the SPA router owns it.
            using (var route = await client.GetAsync(baseUrl + "/login"))
            {
                Assert.Equal(HttpStatusCode.OK, route.StatusCode);
                Assert.Contains("chorus-web18-shell", await route.Content.ReadAsStringAsync());
            }

            // (3) A static asset answers verbatim with its content type.
            using (var asset_ = await client.GetAsync(baseUrl + "/assets/web18-app.js"))
            {
                Assert.Equal(HttpStatusCode.OK, asset_.StatusCode);
                Assert.Equal(asset, await asset_.Content.ReadAsStringAsync());
                Assert.Contains("javascript", asset_.Content.Headers.ContentType?.ToString());
            }

            // (4) The Admin API face is untouched: the login probe still answers behind the
            // operator bearer (POST — the transcoding annotations are POST-only), and
            // anonymously it is still refused.
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, baseUrl + "/admin/api/v1/who-am-i", null, "{}")).StatusCode);
            using (var whoami = await PostAsync(client, baseUrl + "/admin/api/v1/who-am-i", "alice-token", "{}"))
            {
                Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
                Assert.Contains("\"operatorId\":\"gm-alice\"", await whoami.Content.ReadAsStringAsync());
            }

            // (5) An unknown admin path answers an honest API 404 — never the SPA shell.
            using (var unknown = await client.GetAsync(baseUrl + "/admin/api/v1/no-such-route"))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                Assert.DoesNotContain("chorus-web18-shell", await unknown.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            try { Directory.Delete(dist, recursive: true); } catch { /* a wedged host process holds it; the sweep reaps it */ }
        }
    }

    [Fact]
    public async Task WithoutTheDistVariableTheBrowserFaceStaysApiOnly()
    {
        await using var environment = await RedesignM0HostTests.HostEnvironment.CreateAsync();
        await environment.StartAsync("Chorus.ControlServer", environment.ControlPort, "control-server",
            new Dictionary<string, string>
            {
                ["CHORUS_DATA_ENDPOINT"] = $"https://localhost:{environment.DataPort}"
            });
        await environment.StartAsync("Chorus.DataServer", environment.DataPort, "data-server");

        using var client = BrowserClient(environment);
        var baseUrl = $"https://localhost:{environment.BrowserPort}";

        // No static hosting was configured: the shell path answers nothing, and the API
        // face keeps its bearer gate exactly as FIX-WEB-03 left it (POST — the
        // annotations are POST-only).
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(baseUrl + "/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, baseUrl + "/admin/api/v1/who-am-i", null, "{}")).StatusCode);
    }

    /// <summary>The no-certificate HTTP client a browser would use (dynamic test
    /// certificates chain to the per-test root CA).</summary>
    private static HttpClient BrowserClient(RedesignM0HostTests.HostEnvironment environment) =>
        new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url,
        string? token, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
}
