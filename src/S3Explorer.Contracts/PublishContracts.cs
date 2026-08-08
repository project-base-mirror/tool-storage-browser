using System;
using System.Collections.Generic;

namespace S3Explorer.Contracts;

/// <summary>
/// Stable compatibility boundary between automation clients and the S3 Explorer CLI.
/// Product patch versions may change without changing this contract version.
/// </summary>
public static class ContractCompatibility
{
    public const int CurrentApiVersion = 1;
    public const int MinimumSupportedApiVersion = 1;
    public const int MaximumSupportedApiVersion = 1;
    public const int MinimumSupportedManifestSchemaVersion = 1;
    public const int MaximumSupportedManifestSchemaVersion = PublishManifest.CurrentSchemaVersion;

    public static bool SupportsApiVersion(int apiVersion) =>
        apiVersion is >= MinimumSupportedApiVersion and <= MaximumSupportedApiVersion;

    public static bool SupportsManifestSchemaVersion(int schemaVersion) =>
        schemaVersion is >= MinimumSupportedManifestSchemaVersion and <= MaximumSupportedManifestSchemaVersion;
}

/// <summary>
/// Version information returned by <c>s3explorer-cli version --output json</c>.
/// Consumers should check the contract and schema ranges instead of requiring an exact product version.
/// </summary>
public sealed class CliCompatibilityInfo
{
    public string Version { get; set; } = string.Empty;
    public int ContractApiVersion { get; set; } = ContractCompatibility.CurrentApiVersion;
    public int MinimumSupportedContractApiVersion { get; set; } = ContractCompatibility.MinimumSupportedApiVersion;
    public int MaximumSupportedContractApiVersion { get; set; } = ContractCompatibility.MaximumSupportedApiVersion;
    public int ManifestSchemaVersion { get; set; } = PublishManifest.CurrentSchemaVersion;
    public int MinimumSupportedManifestSchemaVersion { get; set; } = ContractCompatibility.MinimumSupportedManifestSchemaVersion;
    public int MaximumSupportedManifestSchemaVersion { get; set; } = ContractCompatibility.MaximumSupportedManifestSchemaVersion;

    public bool SupportsClient(int clientContractApiVersion, int clientManifestSchemaVersion) =>
        clientContractApiVersion >= MinimumSupportedContractApiVersion &&
        clientContractApiVersion <= MaximumSupportedContractApiVersion &&
        clientManifestSchemaVersion >= MinimumSupportedManifestSchemaVersion &&
        clientManifestSchemaVersion <= MaximumSupportedManifestSchemaVersion;
}

/// <summary>Describes why a local publish file differs from its remote manifest entry.</summary>
public enum PublishChangeKind
{
    New,
    Modified,
    Unchanged
}

/// <summary>Controls whether publishing preserves or explicitly changes object ACLs.</summary>
public enum PublishAccessMode
{
    Preserve,
    AnonymousRead,
    Private
}

/// <summary>Controls whether publishing retains remote-only objects or mirrors the local source.</summary>
public enum PublishDeleteMode
{
    None,
    Mirror
}

/// <summary>A versioned manifest shared by the CLI, Unity Editor and game runtime.</summary>
public sealed class PublishManifest
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Project { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public PublishAccessMode AccessMode { get; set; } = PublishAccessMode.Preserve;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<PublishManifestFile> Files { get; set; } = new List<PublishManifestFile>();
}

/// <summary>One immutable file entry in a publish manifest.</summary>
public sealed class PublishManifestFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public PublishObjectHeaders? Headers { get; set; }
}

/// <summary>HTTP headers and user-defined metadata that must be applied to one published object.</summary>
public sealed class PublishObjectHeaders
{
    public string? ContentType { get; set; }
    public string? CacheControl { get; set; }
    public string? ContentEncoding { get; set; }
    public string? ContentDisposition { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

/// <summary>Versioned Header rules consumed by CLI and Unity publish integrations.</summary>
public sealed class PublishHeaderRuleSet
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public PublishObjectHeaders? Defaults { get; set; }
    public List<PublishHeaderRule> Rules { get; set; } = new List<PublishHeaderRule>();
}

/// <summary>A glob-scoped overlay applied after the default publish headers.</summary>
public sealed class PublishHeaderRule
{
    public string Pattern { get; set; } = string.Empty;
    public PublishObjectHeaders Headers { get; set; } = new PublishObjectHeaders();
}

/// <summary>One file in a publish preview.</summary>
public sealed class PublishPlanItem
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public PublishObjectHeaders? Headers { get; set; }
    public PublishChangeKind Change { get; set; }
}

/// <summary>Structured preview consumed by CLI and Unity Editor surfaces.</summary>
public sealed class PublishPlan
{
    public int NewFiles { get; set; }
    public int ModifiedFiles { get; set; }
    public int UnchangedFiles { get; set; }
    public long UploadBytes { get; set; }
    public List<PublishPlanItem> Items { get; set; } = new List<PublishPlanItem>();
}

/// <summary>A path-scoped failure safe to display in automation clients.</summary>
public sealed class OperationFailure
{
    public string Path { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Final result returned by a publish command.</summary>
public sealed class PublishResult
{
    public bool Success { get; set; }
    public string Profile { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public PublishAccessMode AccessMode { get; set; } = PublishAccessMode.Preserve;
    public PublishDeleteMode DeleteMode { get; set; } = PublishDeleteMode.None;
    public int AclUpdatedFiles { get; set; }
    public int UploadedFiles { get; set; }
    public int DeletedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int FailedFiles { get; set; }
    public long UploadedBytes { get; set; }
    public long DeletedBytes { get; set; }
    public string RemoteUri { get; set; } = string.Empty;
    public string CdnUrl { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public long ElapsedMilliseconds { get; set; }
    public List<OperationFailure> Failures { get; set; } = new List<OperationFailure>();
}

/// <summary>Result returned by manifest verification.</summary>
public sealed class VerifyResult
{
    public bool Success { get; set; }
    public string Profile { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public int VerifiedFiles { get; set; }
    public int FailedFiles { get; set; }
    public long VerifiedBytes { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public List<OperationFailure> Failures { get; set; } = new List<OperationFailure>();
}

/// <summary>One CDN URL operation result.</summary>
public sealed class CdnItemResult
{
    public string Path { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Success { get; set; }
    public int? StatusCode { get; set; }
    public long BytesRead { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Attempt { get; set; } = 1;
    public string CacheStatus { get; set; } = string.Empty;
}

/// <summary>Aggregate result for CDN test or warmup commands.</summary>
public sealed class CdnBatchResult
{
    public bool Success { get; set; }
    public string Profile { get; set; } = string.Empty;
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public List<CdnItemResult> Items { get; set; } = new List<CdnItemResult>();
}
