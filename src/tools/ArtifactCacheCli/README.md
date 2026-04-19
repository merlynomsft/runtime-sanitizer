# ArtifactCacheCli

`ArtifactCacheCli` helps cache and restore expensive local build artifacts (for example `artifacts/bin/microsoft.netcore.app.ref`) using GitHub Actions artifacts.

## Build

```bash
dotnet build /home/runner/work/runtime-sanitizer/runtime-sanitizer/src/tools/ArtifactCacheCli/ArtifactCacheCli.csproj
```

## Determine cache candidates

```bash
dotnet run --project /home/runner/work/runtime-sanitizer/runtime-sanitizer/src/tools/ArtifactCacheCli/ArtifactCacheCli.csproj -- \
  determine \
  --repo-root /home/runner/work/runtime-sanitizer/runtime-sanitizer \
  --profile libs-prereqs \
  --target-os linux \
  --target-arch x64 \
  --libraries-configuration Debug
```

## Pack local cache snapshots

```bash
dotnet run --project /home/runner/work/runtime-sanitizer/runtime-sanitizer/src/tools/ArtifactCacheCli/ArtifactCacheCli.csproj -- \
  pack \
  --repo-root /home/runner/work/runtime-sanitizer/runtime-sanitizer \
  --profile libs-prereqs \
  --artifact-prefix runtime-sanitizer-linux-x64-debug-libs \
  --output-dir /tmp/runtime-sanitizer-cache
```

Each entry becomes `<artifact-prefix>-<entry-name>.zip`.

## Restore from GitHub Actions artifacts

Set a token with `actions:read` scope:

```bash
export RUNTIME_SANITIZER_GITHUB_TOKEN='<token>'
```

Then restore:

```bash
dotnet run --project /home/runner/work/runtime-sanitizer/runtime-sanitizer/src/tools/ArtifactCacheCli/ArtifactCacheCli.csproj -- \
  restore \
  --repo-root /home/runner/work/runtime-sanitizer/runtime-sanitizer \
  --owner merlynomsft \
  --repo runtime-sanitizer \
  --profile libs-prereqs \
  --artifact-prefix runtime-sanitizer-linux-x64-debug-libs
```

The tool extracts each archive entry to the original repository-relative location.
