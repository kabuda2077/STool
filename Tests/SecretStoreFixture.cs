using System.IO;
using STool.Core;

namespace STool.Tests;

internal sealed class SecretStoreFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "STool.Tests", Guid.NewGuid().ToString("N"));
    public string KeyPath => Path.Combine(Root, "secure.key");
    public PortableSecretStore Store { get; }

    public SecretStoreFixture()
    {
        Store = new PortableSecretStore(KeyPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
}
