using Amazon.S3;
using Amazon.S3.Model;
using S3Explorer.Core;
using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class S3LifecycleMapperTests
{
    [Fact]
    public void MapperRoundTripsSupportedLifecycleConfiguration()
    {
        var source = new BucketLifecycleConfiguration([
            new BucketLifecycleRule(
                "archive", true, "logs/",
                [new LifecycleTag("team", "platform"), new LifecycleTag("class", "audit")],
                [new S3Explorer.Core.LifecycleTransition(30, LifecycleStorageClass.StandardInfrequentAccess)],
                365,
                [new S3Explorer.Core.LifecycleTransition(90, LifecycleStorageClass.GlacierFlexibleRetrieval)],
                730,
                7)
        ]);

        var sdk = S3LifecycleMapper.ToSdk(source);
        var rule = Assert.Single(sdk.Rules);
        Assert.IsType<LifecycleAndOperator>(rule.Filter.LifecycleFilterPredicate);

        var roundTrip = S3LifecycleMapper.ToCore(sdk);
        Assert.True(BucketLifecycleDocument.AreSemanticallyEquivalent(source, roundTrip));
    }

    [Fact]
    public void MapperRejectsAbsoluteDateRulesInsteadOfDroppingThem()
    {
        var sdk = new LifecycleConfiguration
        {
            Rules =
            [
                new LifecycleRule
                {
                    Id = "date-expiration",
                    Status = LifecycleRuleStatus.Enabled,
                    Filter = new LifecycleFilter
                    {
                        LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = string.Empty }
                    },
                    Expiration = new LifecycleRuleExpiration
                    {
                        Date = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    }
                }
            ]
        };

        var exception = Assert.Throws<NotSupportedException>(() => S3LifecycleMapper.ToCore(sdk));

        Assert.Contains("绝对日期", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapperRejectsTransitionWithoutDaysInsteadOfInventingAValue()
    {
        var sdk = new LifecycleConfiguration
        {
            Rules =
            [
                new LifecycleRule
                {
                    Id = "missing-days",
                    Status = LifecycleRuleStatus.Enabled,
                    Filter = new LifecycleFilter
                    {
                        LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = string.Empty }
                    },
                    Transitions =
                    [
                        new Amazon.S3.Model.LifecycleTransition
                        {
                            StorageClass = S3StorageClass.StandardInfrequentAccess
                        }
                    ]
                }
            ]
        };

        var exception = Assert.Throws<NotSupportedException>(() => S3LifecycleMapper.ToCore(sdk));

        Assert.Contains("缺少天数", exception.Message, StringComparison.Ordinal);
    }
}
