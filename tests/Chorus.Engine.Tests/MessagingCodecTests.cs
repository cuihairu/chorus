using Chorus.Messaging;
using Chorus.Messaging.Ipc;
using Chorus.Protocol;

namespace Chorus.Engine.Tests;

/// <summary>
/// 编解码与信封工厂的 fail-visible 边：JSON null 字面量、空白 sessionId、
/// null body 反序列化都必须显式抛错，不得悄悄产出半状态信封。
/// </summary>
public sealed class MessagingCodecTests
{
    [Fact]
    public void EnvelopeCodec_NullLiteral_Throws()
    {
        // "null" 是合法 JSON：解析成功但信封反序列化为 null → 必须显式拒绝。
        Assert.Throws<InvalidOperationException>(() => JsonEnvelopeCodec.Deserialize("null"u8));
    }

    [Fact]
    public async Task EnvelopeCodec_AsyncNullLiteral_Throws()
    {
        using var stream = new MemoryStream("null"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await JsonEnvelopeCodec.DeserializeAsync(stream, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EnvelopeFactory_BlankSessionId_Throws(string? sessionId)
    {
        Assert.Throws<ArgumentException>(
            () => EnvelopeFactory.Create("test", sessionId!, new { ok = true }));
    }

    [Fact]
    public void EnvelopeFactory_NullBody_DeserializeBodyThrows()
    {
        var env = JsonEnvelopeCodec.Deserialize(
            """{"type":"test","messageId":"m1","sessionId":"s1","createdTimeMs":0,"body":null}"""u8);

        Assert.Throws<InvalidOperationException>(() => EnvelopeFactory.DeserializeBody<GatewayAckCmdBody>(env));
    }

    private sealed record GatewayAckCmdBody(string SessionId, string ClientId, long LastAppliedTickId, long ClientTimeMs);
}
