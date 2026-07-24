# S3-compatible Provider Matrix

The provider matrix validates the same production code path against MinIO and optional S3-compatible services.

## Status semantics

- `Passed`: credentials were configured and the full matrix completed.
- `Failed`: credentials were configured, but at least one verification failed.
- `NotConfigured`: endpoint or credentials were missing. This never counts as a pass.
- `Skipped`: the environment was configured but the scenario could not run for an explicit platform or policy reason.

## Providers and environment prefixes

| Provider | Prefix |
| --- | --- |
| MinIO | `S3EXPLORER_MINIO` |
| Amazon S3 | `S3EXPLORER_AWS` |
| Tencent COS | `S3EXPLORER_TENCENT_COS` |
| Aliyun OSS S3 | `S3EXPLORER_ALIYUN_OSS` |
| Cloudflare R2 | `S3EXPLORER_CLOUDFLARE_R2` |
| Backblaze B2 | `S3EXPLORER_BACKBLAZE_B2` |
| Google Cloud Storage | `S3EXPLORER_GCS` |
| Supabase Storage | `S3EXPLORER_SUPABASE` |

Each prefix supports `_ENDPOINT`, `_REGION`, `_ACCESS_KEY`, `_SECRET_KEY`, optional `_SESSION_TOKEN`, optional `_IGNORE_CERTIFICATE_ERRORS`, and optional `_KNOWN_BUCKET`.

MinIO also accepts the legacy `S3EXPLORER_TEST_*` variables.

## Run

```powershell
pwsh .\scripts\Invoke-S3ProviderMatrix.ps1
```

To require a configured MinIO target:

```powershell
pwsh .\scripts\Invoke-S3ProviderMatrix.ps1 -FailOnRequiredNotConfigured
```

The JSON report is written to `artifacts/provider-matrix.json`.

## Coverage

Configured providers run:

- connection and bucket access;
- object listing with continuation tokens;
- Unicode, spaces, plus signs, and percent signs in keys;
- upload, download, properties, and presigned URL creation;
- copy and move;
- multi-object delete enabled and disabled;
- 20 MiB multipart upload;
- best-effort object, unfinished multipart upload, and owned-bucket cleanup.

Reports and logs must not contain secret keys, session tokens, authorization headers, or presigned query strings.
