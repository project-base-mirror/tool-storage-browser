using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using CoreLifecycleTransition = S3Explorer.Core.LifecycleTransition;
using CoreTag = S3Explorer.Core.LifecycleTag;

namespace S3Explorer.Infrastructure.S3;

internal static class S3LifecycleMapper
{
    public static LifecycleConfiguration ToSdk(BucketLifecycleConfiguration configuration)
    {
        var normalized = BucketLifecycleDocument.Validate(configuration);
        return new LifecycleConfiguration
        {
            Rules = normalized.Rules.Select(rule => new LifecycleRule
            {
                Id = rule.Id,
                Status = rule.Enabled ? LifecycleRuleStatus.Enabled : LifecycleRuleStatus.Disabled,
                Filter = BuildFilter(rule),
                Transitions = rule.Transitions.Select(ToSdkTransition).ToList(),
                Expiration = rule.ExpirationDays is null
                    ? null
                    : new LifecycleRuleExpiration { Days = rule.ExpirationDays.Value },
                NoncurrentVersionTransitions = rule.NoncurrentVersionTransitions
                    .Select(ToSdkNoncurrentTransition).ToList(),
                NoncurrentVersionExpiration = rule.NoncurrentVersionExpirationDays is null
                    ? null
                    : new LifecycleRuleNoncurrentVersionExpiration
                    {
                        NoncurrentDays = rule.NoncurrentVersionExpirationDays.Value
                    },
                AbortIncompleteMultipartUpload = rule.AbortIncompleteMultipartUploadDays is null
                    ? null
                    : new LifecycleRuleAbortIncompleteMultipartUpload
                    {
                        DaysAfterInitiation = rule.AbortIncompleteMultipartUploadDays.Value
                    }
            }).ToList()
        };
    }

    public static BucketLifecycleConfiguration ToCore(LifecycleConfiguration? configuration)
    {
        var rules = (configuration?.Rules ?? []).Select(rule =>
        {
            var (prefix, tags) = ReadFilter(rule);
            EnsureDayBasedRule(rule);
            return new BucketLifecycleRule(
                rule.Id ?? string.Empty,
                string.Equals(rule.Status?.Value, LifecycleRuleStatus.Enabled.Value, StringComparison.Ordinal),
                prefix,
                tags,
                ReadTransitions(rule),
                rule.Expiration is { Days: > 0 } ? rule.Expiration.Days : null,
                ReadNoncurrentTransitions(rule),
                rule.NoncurrentVersionExpiration is { NoncurrentDays: > 0 }
                    ? rule.NoncurrentVersionExpiration.NoncurrentDays
                    : null,
                rule.AbortIncompleteMultipartUpload is { DaysAfterInitiation: > 0 }
                    ? rule.AbortIncompleteMultipartUpload.DaysAfterInitiation
                    : null);
        }).ToArray();
        return BucketLifecycleDocument.Validate(new BucketLifecycleConfiguration(rules));
    }

    private static LifecycleFilter BuildFilter(BucketLifecycleRule rule)
    {
        var operands = new List<LifecycleFilterPredicate>();
        if (rule.Prefix is not null)
            operands.Add(new LifecyclePrefixPredicate { Prefix = rule.Prefix });
        operands.AddRange(rule.Tags.Select(tag => new LifecycleTagPredicate
        {
            Tag = new Amazon.S3.Model.Tag { Key = tag.Key, Value = tag.Value }
        }));

        LifecycleFilterPredicate predicate = operands.Count switch
        {
            0 => new LifecyclePrefixPredicate { Prefix = string.Empty },
            1 => operands[0],
            _ => new LifecycleAndOperator { Operands = operands }
        };
        return new LifecycleFilter { LifecycleFilterPredicate = predicate };
    }

    private static (string? Prefix, IReadOnlyList<CoreTag> Tags) ReadFilter(LifecycleRule rule)
    {
        string? prefix = null;
        var tags = new List<CoreTag>();
        var predicate = rule.Filter?.LifecycleFilterPredicate;
        switch (predicate)
        {
            case null:
                break;
            case LifecyclePrefixPredicate prefixPredicate:
                prefix = string.IsNullOrEmpty(prefixPredicate.Prefix) ? null : prefixPredicate.Prefix;
                break;
            case LifecycleTagPredicate tagPredicate:
                AddTag(tags, tagPredicate);
                break;
            case LifecycleAndOperator and:
                foreach (var operand in and.Operands)
                {
                    switch (operand)
                    {
                        case LifecyclePrefixPredicate prefixOperand:
                            prefix = string.IsNullOrEmpty(prefixOperand.Prefix) ? null : prefixOperand.Prefix;
                            break;
                        case LifecycleTagPredicate tagOperand:
                            AddTag(tags, tagOperand);
                            break;
                        default:
                            throw new NotSupportedException("当前生命周期规则包含对象大小等尚未支持的过滤条件，已阻止编辑以避免配置丢失。");
                    }
                }
                break;
            default:
                throw new NotSupportedException("当前生命周期规则包含尚未支持的过滤条件，已阻止编辑以避免配置丢失。");
        }
        return (prefix, tags);
    }

    private static void AddTag(ICollection<CoreTag> tags, LifecycleTagPredicate predicate)
    {
        if (predicate.Tag is null)
            throw new NotSupportedException("生命周期 Tag 过滤条件缺少 Tag 内容。");
        tags.Add(new CoreTag(predicate.Tag.Key ?? string.Empty, predicate.Tag.Value ?? string.Empty));
    }

    private static IReadOnlyList<CoreLifecycleTransition> ReadTransitions(LifecycleRule rule)
    {
        var values = rule.Transitions ?? [];
        return values.Select(value =>
        {
            if (HasDate(value.DateUtc))
                throw new NotSupportedException("当前生命周期配置包含按绝对日期执行的存储类型转换，已阻止编辑以避免配置丢失。");
            return new CoreLifecycleTransition(value.Days, FromSdkStorageClass(value.StorageClass));
        }).ToArray();
    }

    private static IReadOnlyList<CoreLifecycleTransition> ReadNoncurrentTransitions(LifecycleRule rule)
    {
        var values = rule.NoncurrentVersionTransitions ?? [];
        return values.Select(value =>
        {
            if (value.NewerNoncurrentVersions > 0)
                throw new NotSupportedException("当前生命周期配置使用 NewerNoncurrentVersions 条件，已阻止编辑以避免配置丢失。");
            return new CoreLifecycleTransition(
                value.NoncurrentDays,
                FromSdkStorageClass(value.StorageClass));
        }).ToArray();
    }

    private static void EnsureDayBasedRule(LifecycleRule rule)
    {
        if (rule.Expiration is not null)
        {
            if (HasDate(rule.Expiration.DateUtc))
                throw new NotSupportedException("当前生命周期配置包含按绝对日期过期的规则，已阻止编辑以避免配置丢失。");
            if (rule.Expiration.ExpiredObjectDeleteMarker)
                throw new NotSupportedException("当前生命周期配置包含 ExpiredObjectDeleteMarker，已阻止编辑以避免配置丢失。");
        }
        if (rule.NoncurrentVersionExpiration?.NewerNoncurrentVersions > 0)
            throw new NotSupportedException("当前生命周期配置使用 NewerNoncurrentVersions 条件，已阻止编辑以避免配置丢失。");
    }

    private static bool HasDate(DateTime value) => value != default && value != DateTime.MinValue;

    private static Amazon.S3.Model.LifecycleTransition ToSdkTransition(CoreLifecycleTransition value) => new()
    {
        Days = value.Days,
        StorageClass = ToSdkStorageClass(value.StorageClass)
    };

    private static LifecycleRuleNoncurrentVersionTransition ToSdkNoncurrentTransition(
        CoreLifecycleTransition value) => new()
    {
        NoncurrentDays = value.Days,
        StorageClass = ToSdkStorageClass(value.StorageClass)
    };

    private static S3StorageClass ToSdkStorageClass(LifecycleStorageClass value) => value switch
    {
        LifecycleStorageClass.StandardInfrequentAccess => S3StorageClass.StandardInfrequentAccess,
        LifecycleStorageClass.OneZoneInfrequentAccess => S3StorageClass.OneZoneInfrequentAccess,
        LifecycleStorageClass.IntelligentTiering => S3StorageClass.IntelligentTiering,
        LifecycleStorageClass.GlacierInstantRetrieval => S3StorageClass.GlacierInstantRetrieval,
        LifecycleStorageClass.GlacierFlexibleRetrieval => S3StorageClass.Glacier,
        LifecycleStorageClass.DeepArchive => S3StorageClass.DeepArchive,
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static LifecycleStorageClass FromSdkStorageClass(S3StorageClass value) => value?.Value switch
    {
        "STANDARD_IA" => LifecycleStorageClass.StandardInfrequentAccess,
        "ONEZONE_IA" => LifecycleStorageClass.OneZoneInfrequentAccess,
        "INTELLIGENT_TIERING" => LifecycleStorageClass.IntelligentTiering,
        "GLACIER_IR" => LifecycleStorageClass.GlacierInstantRetrieval,
        "GLACIER" => LifecycleStorageClass.GlacierFlexibleRetrieval,
        "DEEP_ARCHIVE" => LifecycleStorageClass.DeepArchive,
        _ => throw new NotSupportedException($"当前生命周期配置包含尚未支持的存储类型 “{value?.Value ?? "空"}”，已阻止编辑以避免配置丢失。")
    };
}
