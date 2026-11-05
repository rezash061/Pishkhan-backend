# 0014. Container images from the SDK, and one Helm chart

- Status: accepted
- Date: 2026-11-05
- Work package: BE-2.11 (Kubernetes deploy), backend plan 13

## Context

Backend plan 13 asks for one image per host on `mcr.microsoft.com/dotnet/aspnet:10.0` chiseled, non-root, with a read-only root
filesystem. It also asks for a Kubernetes and Helm deploy from Phase 2, and the Migrator as a pre-deploy job. The repository had no
Dockerfiles yet.

## Decision

- Images are built by the .NET SDK (`-t:PublishContainer`), configured once in `src/Hosts/Directory.Build.props`, instead of five
  Dockerfiles. The base is the `noble-chiseled-extra` flavour: chiseled (no shell or package manager, non-root `app` user), plus ICU and
  tzdata, which Persian culture data and the `Asia/Tehran` time zone need. The plain chiseled images run in invariant globalization mode
  and would format Jalali dates and Persian numbers wrongly.
- One chart (`backend/deploy/helm/pishkhan`) renders every host. The Workers image runs as one Deployment per consumer group. The
  Migrator is a Helm hook Job. Infrastructure stays outside the chart, run by its own operators.
- CI renders the chart and checks it against the Kubernetes 1.30 schemas (kubeconform) on every pull request that touches it. It also
  builds every image and checks that it runs as `app` and has no shell.

## Consequences

- No Dockerfile to keep in step with the projects. The image tag, registry and labels are MSBuild properties set by CI.
- Debugging inside a pod uses `kubectl debug` with an ephemeral container, since there is no shell in the image.
- Kubernetes 1.30 or later is required, for the `sleep` pre-stop hook.
