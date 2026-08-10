using System.Reflection;
using System.IO;

namespace STool.Modules.LanTransfer;

internal static class LanTransferWebAssets
{
    public static byte[]? Read(string fileName)
    {
        var assembly = typeof(LanTransferWebAssets).Assembly;
        var suffix = $".LanTransfer.Web.{fileName}";
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName == null)
            return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
