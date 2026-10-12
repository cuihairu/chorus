namespace Chorus.Engine.Session;

public interface ISessionClient
{
    string ClientId { get; }
    ValueTask SendSnapshotAsync(SessionSnapshot snapshot, CancellationToken cancellationToken);
}

