using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Chorus.Control;

/// <summary>Authenticated control RPC. No local authority fallback on an HTTP failure.</summary>
public sealed class ControlClient : IDisposable
{
    private readonly HttpClient _http;
    public string NodeId { get; }
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public ControlClient(IConfiguration config, string nodeId, HttpMessageHandler? handler = null)
    {
        NodeId = nodeId;
        var url = config["Control:Url"] ?? throw new ArgumentException("Control:Url required.");
        var key = config["Control:Key"] ?? throw new ArgumentException("Control:Key required.");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(5);
        _http.DefaultRequestHeaders.Add("X-Control-Key", key);
    }
    public async Task<T> Get<T>(string path, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<T>("internal/" + path, ControlDatabase.Json, ct) ?? throw new JsonException("Empty control response.");
    public async Task<T> Post<T>(string path, object value, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("internal/" + path, value, ControlDatabase.Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ControlDatabase.Json, ct) ?? throw new JsonException("Empty control response.");
    }
    public async Task Send(string path, object value, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("internal/" + path, value, ControlDatabase.Json, ct);
        response.EnsureSuccessStatusCode();
    }
    public Task<RoomRecord> Assign(string id, CancellationToken ct) => Post<RoomRecord>("rooms/assign", new RoomAssignment(id), ct);
    public Task<RoomRecord> Room(string id, CancellationToken ct = default, bool checkpoint = false) => Get<RoomRecord>($"rooms/{Uri.EscapeDataString(id)}?checkpoint={checkpoint.ToString().ToLowerInvariant()}", ct);
    public void Dispose() => _http.Dispose();
}
