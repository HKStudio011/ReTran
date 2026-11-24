using System.Xml.Linq;

public class BrandAssetTests
{
    [Fact]
    public void MauiIcon_UsesFoldedABrandAssets()
    {
        string repositoryRoot = FindRepositoryRoot();
        string backgroundText = File.ReadAllText(Path.Combine(
            repositoryRoot, "ReTran.App", "Resources", "AppIcon", "appicon.svg"));
        string foregroundText = File.ReadAllText(Path.Combine(
            repositoryRoot, "ReTran.App", "Resources", "AppIcon", "appiconfg.svg"));
        string projectText = File.ReadAllText(Path.Combine(
            repositoryRoot, "ReTran.App", "ReTran.App.csproj"));

        XDocument background = XDocument.Parse(backgroundText);
        XDocument foreground = XDocument.Parse(foregroundText);
        XDocument project = XDocument.Parse(projectText);

        Assert.Equal("0 0 456 456", background.Root?.Attribute("viewBox")?.Value);
        Assert.Equal("0 0 456 456", foreground.Root?.Attribute("viewBox")?.Value);
        Assert.Contains("#07201E", backgroundText, StringComparison.Ordinal);
        Assert.Equal("#07201E", project.Descendants("MauiIcon").Single().Attribute("Color")?.Value);
        Assert.Contains("#EFEAD9", foregroundText, StringComparison.Ordinal);
        Assert.Contains("#FFB020", foregroundText, StringComparison.Ordinal);
        Assert.DoesNotContain(foreground.Descendants(), element => element.Name.LocalName == "text");
        Assert.DoesNotContain("#512BD4", foregroundText, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReTran Project.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not find 'ReTran Project.slnx' above '{AppContext.BaseDirectory}'.");
    }
}
