using System;
using System.Collections.Generic;

namespace S3Explorer.Contracts;

/// <summary>Describes why a local publish file differs from its remote manifest entry.</summary>
public enum PublishChangeKind
{
    New,
    Modified,
    Unchanged
}

/// <summary>A versioned manifest shared by the CLI, Unity Editor and game runtime.</summary>
public sealed class PublishManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Project { get; set; } = string.Empty;
    public string Product { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Profile { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public List<PublishManifestFile> Files { get; set; } = new List<PublishManifestFile>();
}

/// <summary>One immutable file entry in a publish manifest.</summary>
public sealed class PublishManifestFile
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>One file in a publish preview.</summary>
public sealed class PublishPlanItem
{
    public string Path { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
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
    public int UploadedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public int FailedFiles { get; set; }
    public long UploadedBytes { get; set; }
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
