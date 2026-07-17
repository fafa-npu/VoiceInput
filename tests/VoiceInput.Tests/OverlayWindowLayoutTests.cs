using System.Xml.Linq;
using VoiceInput.Models;
using VoiceInput.Views;

namespace VoiceInput.Tests;

public sealed class OverlayWindowLayoutTests
{
    [Fact]
    public void OverlayWindowDefinesControlPodLayout()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        string? path = null;
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "src", "VoiceInput", "Views", "OverlayWindow.xaml");
            if (File.Exists(candidate))
            {
                path = candidate;
                break;
            }

            directory = directory.Parent;
        }

        Assert.True(path is not null, $"Could not find src/VoiceInput/Views/OverlayWindow.xaml from {AppContext.BaseDirectory}.");
        XDocument document = XDocument.Load(path!);
        XElement window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("True", (string?)window.Attribute("AllowsTransparency"));
        Assert.Equal("Transparent", (string?)window.Attribute("Background"));
        Assert.Equal("True", (string?)window.Attribute("Topmost"));
        Assert.Equal("False", (string?)window.Attribute("ShowActivated"));
        Assert.Equal("820", (string?)window.Attribute("Width"));
        Assert.Equal("150", (string?)window.Attribute("Height"));

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] names = ["WaveModule", "InformationPod", "PhaseLabel", "Label", "LiveIndicator"];
        string[] missing = names
            .Where(name => !document.Descendants().Any(element => (string?)element.Attribute(x + "Name") == name))
            .ToArray();

        Assert.True(missing.Length == 0, $"Missing named elements: {string.Join(", ", missing)}");

        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);

        XElement waveModule = Named("WaveModule");
        Assert.Equal("64", (string?)waveModule.Attribute("Width"));
        Assert.Equal("#DFFF52", (string?)waveModule.Attribute("Background"));
        Assert.True(double.Parse((string)waveModule.Attribute("CornerRadius")!) <= 18);

        XElement informationPod = Named("InformationPod");
        Assert.Equal("54", (string?)informationPod.Attribute("Height"));
        Assert.Equal("#171A1D", (string?)informationPod.Attribute("Background"));

        Assert.Equal("580", (string?)Named("Label").Attribute("MaxWidth"));
        Assert.DoesNotContain(document.Descendants(), element => element.Name.LocalName == "LinearGradientBrush");
    }

    [Theory]
    [InlineData(OverlayPosition.Top, 100, 1000, 150, 1.0, 164)]
    [InlineData(OverlayPosition.Bottom, 100, 1000, 150, 1.0, 786)]
    [InlineData(OverlayPosition.Top, 0, 1600, 225, 1.5, 96)]
    [InlineData(OverlayPosition.Bottom, 0, 1600, 225, 1.5, 1279)]
    public void OverlayPositionUsesTheConfiguredWorkAreaEdge(
        OverlayPosition position,
        int workTop,
        int workBottom,
        int physicalHeight,
        double scale,
        int expected) =>
        Assert.Equal(
            expected,
            OverlayWindow.CalculateVerticalPosition(position, workTop, workBottom, physicalHeight, scale));
}
