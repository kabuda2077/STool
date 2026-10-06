using System.Collections;
using System.Resources;
using STool.Modules.LanTransfer;
using STool.Modules.Screenshot;
using Xunit;

namespace STool.Tests;

public class EmbeddedResourceTests
{
    [Fact]
    public void WpfResources_KeepTheirProjectRelativeNames()
    {
        var assembly = typeof(CaptureOverlay).Assembly;
        var resourceName = Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".g.resources", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new ResourceReader(stream);
        var keys = reader.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var key in new[]
        {
            "styles/colors.baml", "styles/typography.baml", "styles/buttons.baml", "styles/windows.baml",
            "resources/stool.ico", "modules/screenshot/captureoverlay.baml"
        })
        {
            Assert.Contains(key, keys);
        }
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("app.js")]
    [InlineData("style.css")]
    public void TransferWebAssets_AreNonemptyEmbeddedResources(string file)
    {
        var content = LanTransferWebAssets.Read(file);
        Assert.NotNull(content);
        Assert.NotEmpty(content);
    }
}
