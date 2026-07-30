using S3Explorer.Core;

namespace S3Explorer.App;

internal sealed record CdnSpecifiedTargetChoice(
    CdnResolvedTarget Target,
    string Label,
    string ToolTip);

internal static class CdnSpecifiedTargetMenu
{
    public static IReadOnlyList<CdnSpecifiedTargetChoice> Build(
        IReadOnlyList<CdnResolvedTarget> targets) => targets
        .Select(target => new CdnSpecifiedTargetChoice(
            target,
            target.Binding.IsDefault ? $"{target.Profile.Name}（默认）" : target.Profile.Name,
            target.Url.AbsoluteUri))
        .ToArray();
}
