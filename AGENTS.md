# S3 Explorer project rules

## Remote release verification

- Use `scripts/Verify-RemoteRelease.ps1` in its default digest-first mode for routine release acceptance.
- Do not download or repeatedly retry large GitHub Release assets for verification. This includes every MSI, the self-contained ZIP, and any other asset of 5 MiB or more.
- Verify large assets by comparing the GitHub Release API `sha256:` digest with the matching entry in the small `SHA256SUMS.txt` asset. A missing or mismatched digest fails the release check.
- Small metadata, the Contracts SDK, and the ordinary framework-dependent ZIP may still be downloaded for structure, version, and smoke checks.
- Do not use `-FullDownload` or manually download large assets unless the user explicitly requests byte-level verification in the current task.

