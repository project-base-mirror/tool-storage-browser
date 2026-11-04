namespace S3Explorer.Core;

public enum ObjectMetadataCopyFailure
{
    ConditionNotMet,
    OutcomeUnknown
}

public sealed class ObjectMetadataCopyException : IOException
{
    public ObjectMetadataCopyFailure Failure { get; }

    public ObjectMetadataCopyException(ObjectMetadataCopyFailure failure, Exception innerException)
        : base(failure == ObjectMetadataCopyFailure.ConditionNotMet
            ? "Metadata 另存的复制条件未满足：目标可能已存在，或发生了并发更改。请先刷新并检查目标，再选择其他新 Key；不会自动覆盖或删除对象。此前若有超时或重试，目标也可能已由此前请求创建。"
            : "Metadata 另存结果尚未确认，目标可能已经创建。请先刷新并检查目标，不要立即改用其他 Key 重复保存；应用不会在此错误后再次发起复制、覆盖或删除对象。", innerException)
    {
        Failure = failure;
    }
}
