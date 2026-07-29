using Xunit;

namespace S3Explorer.Infrastructure.S3.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConfiguredProviderFactAttribute : FactAttribute
{
    public ConfiguredProviderFactAttribute(string? providerId = null)
    {
        var provider = string.IsNullOrWhiteSpace(providerId)
            ? ProviderMatrixCase.Selected()
            : ProviderMatrixCase.All.Single(item =>
                string.Equals(item.Id, providerId, StringComparison.OrdinalIgnoreCase));
        var configuration = provider.Resolve();
        if (configuration.IsConfigured)
            return;

        Skip = configuration.ToReportJson(
            ProviderMatrixStatus.NotConfigured,
            "Required endpoint/access-key/secret-key environment variables are not configured.");
    }
}
