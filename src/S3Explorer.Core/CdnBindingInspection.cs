namespace S3Explorer.Core;

public sealed record CdnBindingInspectionResult(
    S3ObjectEntry SourceObject,
    Uri Url,
    CdnProbeResult Probe);

public static class CdnBindingInspector
{
    public static async Task<CdnBindingInspectionResult?> InspectAsync(
        CdnProfile profile,
        CdnBinding binding,
        Func<string, string?, CancellationToken, Task<PagedObjectResult>> loadPage,
        Func<Uri, CancellationToken, Task<CdnProbeResult>> probeHead,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(loadPage);
        ArgumentNullException.ThrowIfNull(probeHead);

        await foreach (var sourceObject in RecursiveObjectListing.EnumerateFilesAsync(
                           binding.SourcePrefix,
                           100,
                           10_000,
                           loadPage,
                           cancellationToken))
        {
            var url = CdnUrlMapper.BuildUrl(profile, binding, sourceObject.Key);
            var probe = await probeHead(url, cancellationToken).ConfigureAwait(false);
            return new CdnBindingInspectionResult(sourceObject, url, probe);
        }
        return null;
    }
}
