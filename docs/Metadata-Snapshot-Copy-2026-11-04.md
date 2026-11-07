# Metadata snapshot copy (2026-11-04)

> 2026-11-07 更新：本文保留 11 月 4 日修复时的边界；原地/批量 Metadata 与文本编辑的后续支持范围见 [当前编辑说明](Editing-and-Cdn-Fixes-2026-11-07.md)。固定版本另存限制未改变。

## User workflow

The single-object Properties → Metadata page now allows eligible AWS objects to be edited and **saved as a new object** in the same bucket. The user must choose a different key and confirm the selected immutable source version, fees, and that the new key is governed by its bucket policy and lifecycle rules. This is a snapshot copy, not an in-place edit. The source remains untouched. A current destination object cannot be overwritten.

In-place and batch metadata rewrites remain disabled. ETags identify content, not every metadata/ACL/tag/protection change. A refreshed HEAD followed by a self-copy is not a transaction over all these attributes. Copying an old source version over the current source key can also resurrect old data.

## Enforced scope

- AWS S3 only. No automatic fallback for compatible providers.
- A non-null immutable source version and nonempty ETag; both version and ETag are applied to CopyObject. A version-specific HEAD must match the original version, ETag, and size.
- At most 5 GiB, using a single CopyObject. No multipart fallback.
- Explicit SSE-S3/AES256 only, specified again in the destination request. SSE-KMS, DSSE-KMS, SSE-C, bucket keys, and unknown encryption states are refused.
- The source must not report retention or legal hold, and the bucket must return the specific AWS `ObjectLockConfigurationNotFoundError` with HTTP 404. Generic 404, missing permissions, unknown results, and any returned lock configuration fail closed.
- Object Ownership must be exactly BucketOwnerEnforced, because CopyObject does not preserve arbitrary ACLs. The copy requests bucket-owner-full-control, which is accepted in ACL-disabled buckets. Bucket configuration remains subject to administration; this workflow does not lock bucket settings.
- Missing/unrepresentable metadata and an unparseable stored Expires header are refused.
- Destination key is preserved exactly, including leading/repeated slashes and spaces. `If-None-Match: *` checks **current** destination absence. A historical key whose current version is a delete marker may receive a new copy; no historical version is deleted.

The destination preserves the edited snapshot's five supported headers and user metadata, plus source Content-Language, website redirect, storage class, and explicit SSE-S3. Tags use server-side COPY of the source version's values **at execution time**; a version ID does not freeze tags or other independently mutable object settings. AWS CopyObject also defaults annotations to COPY. The new object receives a new creation time/version and its own lifecycle/policy evaluation; this is not a clone of all system-generated state.

A conflict, timeout, cancellation, or permission failure never triggers an unguarded write, multipart fallback, or deletion. If the copy response is lost, the new key might already exist. Refresh the destination before retrying; a retry remains create-only.

## Preservation and recovery improvements

- Property loading and Metadata save-as preserve the header and metadata **strings returned by the SDK**, including leading/trailing whitespace in values. The application no longer applies upload-default trimming to an existing snapshot. This does not promise preservation of arbitrary raw HTTP bytes that the SDK or HTTP stack already normalized. Existing upload-default normalization is unchanged; metadata validation still rejects header injection, duplicates, and values exceeding the 2 KiB limit.
- Saving commits an active metadata grid cell before reading it, avoiding silent loss of the final edit. Merely reading or closing the editor does not issue storage writes.
- Suggested new keys respect S3's 1,024-byte UTF-8 limit without splitting Unicode characters or suggesting the source key itself. Short source keys, including slashes and spaces, remain exact prefixes of the suggestion.
- Destination 409/412 responses and uncertain post-dispatch timeouts, cancellation, and server failures now explain how to inspect the destination before another attempt. The application never falls back to an overwrite or deletes either key. Because the AWS SDK can retry internally, even a final 412 can follow an earlier successful attempt whose response was lost. AWS status and request ID remain available in the error dialog.
- Version history provides recovery, not a compare-and-swap token for the current metadata revision. This improvement intentionally does not re-enable in-place or batch replacement.

## SDK regression

AWS SDK v4's HEAD unmarshaller places the Expires wire value in `ExpiresString`, not `Headers.Expires`. Property loading now parses that raw string as invariant UTC and retains invalid raw values for inspection. Writes set `Headers.Expires` only when a value exists, avoiding the SDK setter's year-0001 behavior on null assignment.

The existing external text-edit workflow also consumes these newly exposed fields. It stays read-only for nonempty Content-Language/website redirects, incomplete metadata, or invalid Expires because its upload model cannot preserve them. Bucket-key and retention-date protection is refused. Freshness checks compare all newly exposed fields even when ETag is unchanged. Ordinary modeled headers, including a valid Expires value, remain editable; the general upload-header API was not widened.

## Verification

New isolated tests cover request marshalling, exact keys, immutable source identity, source/target guards, preservation, hidden headers, valid/invalid Expires, refused protection/encryption/configuration states, unsupported providers, conflicts, and uncertain outcomes. Core and UI tests cover capability gating and the explicit save-as workflow. Existing fail-closed in-place tests remain unchanged. Local `git diff --check` passes. This executor has no .NET SDK, so compilation and test execution require CI; no real storage or credentials were used.

## Primary sources

- [AWS CopyObject API](https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html): destination conditions, source versions/conditions, default tag and annotation copying, ACL reset, encryption overrides, redirect special handling.
- [AWS conditional writes](https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-writes.html): create-only current-version semantics and conflict handling.
- [AWS object metadata](https://docs.aws.amazon.com/AmazonS3/latest/userguide/UsingMetadata.html): immutable user metadata and missing-metadata reporting.
- [AWS versioning workflows](https://docs.aws.amazon.com/AmazonS3/latest/userguide/versioning-workflows.html): new versions preserve old objects, while tag changes do not create a new version.
- [AWS object ownership](https://docs.aws.amazon.com/AmazonS3/latest/userguide/about-object-ownership.html): BucketOwnerEnforced and accepted ACL headers.
- [AWS HEAD response SDK contract](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TGetObjectMetadataResponse.html): MissingMeta, encryption/lock fields and permission-dependent visibility.
- [SDK HEAD unmarshaller](https://github.com/aws/aws-sdk-net/blob/main/sdk/src/Services/S3/Custom/Model/Internal/MarshallTransformations/GetObjectMetadataResponseUnmarshaller.cs) and [HeadersCollection](https://github.com/aws/aws-sdk-net/blob/main/sdk/src/Services/S3/Custom/Model/HeadersCollection.cs): Expires handling.
- [Cloudflare R2 extensions](https://developers.cloudflare.com/r2/api/s3/extensions/): source and destination CopyObject checks are not atomic with each other; this is not a basis for restoring general in-place writes.
