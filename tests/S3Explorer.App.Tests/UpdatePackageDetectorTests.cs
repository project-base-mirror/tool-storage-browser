using S3Explorer.App;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class UpdatePackageDetectorTests
{
    [Theory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData(null, 0)]
    public void PortableFlavorUsesEmbeddedPublishMetadata(string? value, int expected)
    {
        Assert.Equal((UpdatePackageKind)expected, UpdatePackageDetector.PortableKind(value));
    }

    [Theory]
    [InlineData(@"D:\Custom Apps\S3 Explorer\S3Explorer.exe", @"D:\Custom Apps\S3 Explorer", true)]
    [InlineData(@"D:\Custom Apps\S3 Explorer\S3Explorer.exe", @"d:\custom apps\s3 explorer\", true)]
    [InlineData(@"D:\Portable\S3Explorer.exe", @"C:\Program Files\S3 Explorer", false)]
    [InlineData(@"D:\Installed\Portable\S3Explorer.exe", @"D:\Installed", false)]
    [InlineData(null, @"D:\Installed", false)]
    public void RegistrationMustMatchActualApplicationDirectory(string? application, string installed, bool expected)
    {
        Assert.Equal(expected, UpdatePackageDetector.MatchesApplicationDirectory(application, installed));
    }
}
