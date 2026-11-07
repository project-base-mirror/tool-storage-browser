using System.Text.Json;
using System.Text.Json.Serialization;
using S3Explorer.Cli;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Cli.Tests;

public sealed class FolderSyncOutputTests
{
    [Fact]
    public void TextOutputExplainsSkippedLinksWithoutRequiringInteraction()
    {
        var output = Program.FormatPlan(Plan());
        Assert.Contains("跳过 1 个本地链接", output, StringComparison.Ordinal);
        Assert.Contains("保持原样且不传播删除", output, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutputIncludesSkipCountAndDoesNotInventALinkFileSize()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.SerializeToElement(Program.PlanView(Plan()), options);
        Assert.Equal(1, json.GetProperty("skippedLocalLinkCount").GetInt32());
        var linked = json.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("relativePath").GetString() == "linked");
        Assert.False(linked.TryGetProperty("localSize", out _));
        Assert.Contains("符号链接", linked.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    private static FolderSyncPlan Plan() => FolderSyncPlanner.Analyze(new FolderSyncJob
    {
        Name = "output test", LocalDirectory = Path.GetTempPath(), ProfileId = Guid.NewGuid(),
        ProfileName = "test", Bucket = "bucket", PropagateDeletions = true
    },
        [new("linked", 0, DateTimeOffset.MinValue) { IsReparsePoint = true }],
        [new("linked/file.txt", 4, DateTimeOffset.UtcNow)]);
}
