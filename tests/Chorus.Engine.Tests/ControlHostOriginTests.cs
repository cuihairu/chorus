using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Chorus.Engine.Tests;

public sealed class ControlHostOriginTests
{
    [Fact]
    public async Task ConfiguredPublicHttpsOriginIsAcceptedAndSetsSecureCookie()
    {
        var (factory, dir) = CreateFactory();
        try
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { Name = "admin", Password = "test-password" })
            };
            request.Headers.TryAddWithoutValidation("Origin", "https://admin.example.test");

            using var response = await client.SendAsync(request);

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => value.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        }
        finally { DisposeFactory(factory, dir); }
    }

    [Fact]
    public async Task UnconfiguredOriginIsRejected()
    {
        var (factory, dir) = CreateFactory();
        try
        {
            using var client = factory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { Name = "admin", Password = "test-password" })
            };
            request.Headers.TryAddWithoutValidation("Origin", "https://attacker.example.test");

            using var response = await client.SendAsync(request);

            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally { DisposeFactory(factory, dir); }
    }

    private static (WebApplicationFactory<global::Chorus.ControlHost.Program> Factory, string Directory) CreateFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chorus-origin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new Dictionary<string, string?>
        {
            ["Control:Key"] = "test-control-key-with-more-than-24-characters",
            ["Control:Database"] = Path.Combine(dir, "control.db"),
            ["Admin:PublicOrigin"] = "https://admin.example.test",
            ["Admin:Users:0:Name"] = "admin",
            ["Admin:Users:0:Role"] = "admin",
            ["Admin:Users:0:PasswordHash"] = new PasswordHasher<string>().HashPassword("admin", "test-password")
        };
        var factory = new WebApplicationFactory<global::Chorus.ControlHost.Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        });
        return (factory, dir);
    }

    private static void DisposeFactory(WebApplicationFactory<global::Chorus.ControlHost.Program> factory, string dir)
    {
        factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }
}
