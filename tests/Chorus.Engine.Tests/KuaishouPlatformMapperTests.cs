using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chorus.Adapters.Platforms;
using Chorus.Protocol.Models;

namespace Chorus.Engine.Tests;

public sealed class KuaishouPlatformMapperTests
{
    private const long IngestTimeMs = 1234;
    // 32-byte key (base64) as required by the official doc: AES key = base64 decode, IV = first 16 bytes.
    private const string Base64Key = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static (string Envelope, string InnerJson) BuildEnvelope(
        string msgId,
        string innerJson,
        string? key = Base64Key)
    {
        var encrypted = key == null
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(innerJson))
            : Encrypt(innerJson, key);

        var envelope = JsonSerializer.Serialize(new
        {
            encryptedMsg = encrypted,
            msgId,
            componentAppId = "ks-app",
            timestamp = 1700000000000L,
        });
        return (envelope, innerJson);
    }

    private static string Encrypt(string plain, string base64Key)
    {
        var key = Convert.FromBase64String(base64Key);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = key.AsSpan(0, 16).ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(Encoding.UTF8.GetBytes(plain), 0, Encoding.UTF8.GetByteCount(plain));
        return Convert.ToBase64String(cipher);
    }

    [Fact]
    public void TryMap_DecryptsAndMapsComment()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);
        var (envelope, _) = BuildEnvelope("msg-1", """{"event":"comment","session_id":"s1","user_id":"u1","content":"hi"}""");

        var ev = mapper.TryMap(PlatformNames.Kuaishou, envelope, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal("msg-1", ev!.EventId);
        Assert.Equal(PlatformNames.Kuaishou, ev.Platform);
        Assert.Equal("s1", ev.SessionId);
        Assert.Equal("u1", ev.UserId);
        Assert.Equal(AudienceEventKind.Comment, ev.Kind);
        Assert.Equal("hi", ev.Text);
    }

    [Fact]
    public void TryMap_DecryptsAndMapsGift()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);
        var (envelope, _) = BuildEnvelope("msg-2", """{"event":"gift","session_id":"s1","user_id":"u1","gift_id":"g1","gift_count":2,"gift_value":5}""");

        var ev = mapper.TryMap(PlatformNames.Kuaishou, envelope, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Gift, ev!.Kind);
        Assert.Equal("g1", ev.GiftId);
        Assert.Equal(2, ev.GiftCount);
        Assert.Equal(5, ev.GiftValue);
    }

    [Fact]
    public void TryMap_UnknownInnerEventType_DoesNotCrash_MapsToUnknownKind()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);
        var (envelope, _) = BuildEnvelope("msg-3", """{"event":"follow","session_id":"s1","user_id":"u1"}""");

        var ev = mapper.TryMap(PlatformNames.Kuaishou, envelope, IngestTimeMs);

        Assert.NotNull(ev);
        Assert.Equal(AudienceEventKind.Unknown, ev!.Kind);
    }

    [Fact]
    public void TryMap_MissingEncryptKey_FailClosed()
    {
        var mapper = new KuaishouPlatformEventMapper(messageEncryptKey: null);
        var (envelope, _) = BuildEnvelope("msg-4", """{"event":"comment","session_id":"s1"}""");

        Assert.Null(mapper.TryMap(PlatformNames.Kuaishou, envelope, IngestTimeMs));
    }

    [Fact]
    public void TryMap_WrongEncryptKey_ReturnsNull()
    {
        var mapper = new KuaishouPlatformEventMapper("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");
        var (envelope, _) = BuildEnvelope("msg-5", """{"event":"comment","session_id":"s1"}""");

        Assert.Null(mapper.TryMap(PlatformNames.Kuaishou, envelope, IngestTimeMs));
    }

    [Fact]
    public void TryMap_PlaintextEnvelope_Rejected()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);

        Assert.Null(mapper.TryMap(PlatformNames.Kuaishou, """{"event":"comment","session_id":"s1"}""", IngestTimeMs));
    }

    [Fact]
    public void TryMap_MissingMsgId_ReturnsNull()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);

        Assert.Null(mapper.TryMap(PlatformNames.Kuaishou, """{"encryptedMsg":"abc"}""", IngestTimeMs));
    }

    [Fact]
    public void TryMap_MalformedJson_ReturnsNull()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);

        Assert.Null(mapper.TryMap(PlatformNames.Kuaishou, "not json", IngestTimeMs));
    }

    [Fact]
    public void TryMap_OtherPlatform_ReturnsNull()
    {
        var mapper = new KuaishouPlatformEventMapper(Base64Key);
        var (envelope, _) = BuildEnvelope("msg-6", """{"event":"comment","session_id":"s1"}""");

        Assert.Null(mapper.TryMap(PlatformNames.Douyin, envelope, IngestTimeMs));
    }
}
