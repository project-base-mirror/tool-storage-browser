using Xunit;
using Xunit.Abstractions;

namespace S3Explorer.Infrastructure.S3.Tests;

public sealed class MinioIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public MinioIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Crud_flow_runs_against_explicit_test_instance()
    {
        var previous = Environment.GetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", "minio");
            await new ProviderMatrixIntegrationTests(_output)
                .Configured_provider_runs_compatibility_matrix_and_cleans_resources();
        }
        finally
        {
            Environment.SetEnvironmentVariable("S3EXPLORER_MATRIX_PROVIDER", previous);
        }
    }
}
