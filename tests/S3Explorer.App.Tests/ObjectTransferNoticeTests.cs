using S3Explorer.Core;
using Xunit;

namespace S3Explorer.App.Tests;

public sealed class ObjectTransferNoticeTests
{
    [Fact]
    public void MoveNoticeExplainsContentConditionWithoutPromisingAtomicRename()
    {
        var text = ObjectTransferDialog.BuildTransferNotice(move: true, itemCount: 2);
        Assert.Contains("移动 2 个选中项", text);
        Assert.Contains("先复制，再按内容 ETag 条件删除", text);
        Assert.Contains("不是原子重命名", text);
        Assert.Contains("Metadata、标签和 ACL", text);
        Assert.Contains("不受 ETag 保护", text);
    }

    [Theory]
    [InlineData(S3ServiceType.MinIO)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public void CompatibleMoveNoticeDisclosesConcurrencyRisk(S3ServiceType serviceType)
    {
        var text = ObjectTransferDialog.BuildTransferNotice(true, 2, serviceType);
        Assert.Contains(S3ProviderCapabilityRegistry.For(serviceType).ConditionalDelete.Reason, text);
        Assert.DoesNotContain("先复制，再按内容 ETag 条件删除", text);
        Assert.Contains("竞争窗口", text);
        Assert.DoesNotContain("禁用", text);
    }

    [Theory]
    [InlineData(S3ServiceType.CloudflareR2)]
    [InlineData(S3ServiceType.SupabaseStorage)]
    public void RelayCopyNoticeExplainsTrafficAndDestinationRace(S3ServiceType serviceType)
    {
        var text = ObjectTransferDialog.BuildTransferNotice(false, 1, serviceType);
        Assert.Contains("本机分片中转", text);
        Assert.Contains("下载与上传流量", text);
        Assert.Contains("不保证并发时原子非覆盖", text);
    }

    [Fact]
    public void CopyNoticeDoesNotImplySourceDeletion()
    {
        var text = ObjectTransferDialog.BuildTransferNotice(move: false, itemCount: 2);
        Assert.Contains("复制 2 个选中项", text);
        Assert.DoesNotContain("删除", text);
        Assert.DoesNotContain("移动", text);
    }
}
