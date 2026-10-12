namespace Chorus.Engine.Abstractions;

public interface IGameModuleFactory
{
    IGameModule Create(string sessionId);
}

