namespace StoryForge.Engine.Secrets;

/// <summary>Where secrets live: the OS credential store, never a settings file or the database.</summary>
internal interface ISecretStore
{
    string? Read(string name);

    /// <summary>Stores the secret; null or empty removes it.</summary>
    void Write(string name, string? value);
}

/// <summary>Used on a platform without a credential store implementation yet (server mode, later).</summary>
internal sealed class UnsupportedSecretStore : ISecretStore
{
    public string? Read(string name) => null;

    public void Write(string name, string? value) =>
        throw new PlatformNotSupportedException("This platform has no credential store for StoryForge X secrets yet.");
}
