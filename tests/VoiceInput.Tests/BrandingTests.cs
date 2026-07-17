using System.Drawing;
using System.Xml.Linq;
using VoiceInput.Services;

namespace VoiceInput.Tests;

public sealed class BrandingTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void VoiceCursorIconUsesSelectedPaletteAtTraySizes(int size)
    {
        using Icon icon = TrayIconFactory.CreateVoiceCursorIcon(size);
        using Bitmap bitmap = icon.ToBitmap();

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
        AssertColorPresent(bitmap, ColorTranslator.FromHtml("#DFFF52"));
        AssertColorPresent(bitmap, ColorTranslator.FromHtml("#171A1D"));
        AssertColorPresent(bitmap, ColorTranslator.FromHtml("#237A52"));
    }

    [Fact]
    public void MigratesLegacyShortcutToGujigujiName()
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoiceInput.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string legacy = Path.Combine(directory, "VoiceInput.lnk");
        string branded = Path.Combine(directory, "gujiguji.lnk");
        File.WriteAllText(legacy, "shortcut target");

        AppController.MigrateLegacyShortcut(directory);

        Assert.False(File.Exists(legacy));
        Assert.Equal("shortcut target", File.ReadAllText(branded));
    }

    [Fact]
    public void PublicBrandChangesWhileTechnicalIdentityRemainsStable()
    {
        string root = FindRepositoryRoot();
        string project = File.ReadAllText(Path.Combine(root, "src", "VoiceInput", "VoiceInput.csproj"));
        string updater = File.ReadAllText(Path.Combine(root, "src", "VoiceInput", "Services", "UpdateService.cs"));
        string app = File.ReadAllText(Path.Combine(root, "src", "VoiceInput", "App.xaml.cs"));
        string controller = File.ReadAllText(Path.Combine(root, "src", "VoiceInput", "AppController.cs"));

        Assert.Contains("<AssemblyName>VoiceInput</AssemblyName>", project);
        Assert.Contains("<Product>gujiguji</Product>", project);
        Assert.Contains("<ApplicationIcon>Assets\\gujiguji.ico</ApplicationIcon>", project);
        Assert.Contains("LogicalName=\"gujiguji.install.ps1\"", project);
        Assert.Contains("AssetName = \"VoiceInput.exe\"", updater);
        Assert.Contains("VoiceInput_SingleInstance_8b1c2d", app);
        Assert.Contains("gujiguji error", app);
        Assert.Contains("gujiguji —", controller);
        Assert.Contains("RefreshInstalledUninstaller();", controller);

        AssertNoLegacyDisplayText(Path.Combine(root, "src", "VoiceInput", "Views", "FirstRunWindow.xaml"));
        AssertNoLegacyDisplayText(Path.Combine(root, "src", "VoiceInput", "Views", "SettingsWindow.xaml"));
    }

    private static void AssertColorPresent(Bitmap bitmap, Color expected)
    {
        bool present = false;
        for (int y = 0; y < bitmap.Height && !present; y++)
        for (int x = 0; x < bitmap.Width; x++)
        {
            Color actual = bitmap.GetPixel(x, y);
            if (actual.A > 0
                && Math.Abs(actual.R - expected.R) <= 12
                && Math.Abs(actual.G - expected.G) <= 12
                && Math.Abs(actual.B - expected.B) <= 12)
            {
                present = true;
                break;
            }
        }

        Assert.True(present, $"Expected #{expected.R:X2}{expected.G:X2}{expected.B:X2} in {bitmap.Width}px icon.");
    }

    private static void AssertNoLegacyDisplayText(string path)
    {
        XDocument document = XDocument.Load(path);
        string[] stale = document.Descendants()
            .Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration
                && attribute.Name.LocalName != "Class"
                && attribute.Value.Contains("VoiceInput", StringComparison.OrdinalIgnoreCase))
            .Select(attribute => $"{attribute.Name}={attribute.Value}")
            .ToArray();

        Assert.Empty(stale);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "VoiceInput", "VoiceInput.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the VoiceInput repository root.");
    }
}
