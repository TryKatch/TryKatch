# Aspire and Docker Desktop on macOS: compatibility research

Reviewed: 2026-09-11. Scope: Apple Silicon development machines, Aspire AppHost startup, Docker detection, ports and storage. This is a source-based investigation, not a reproduction on the affected laptop. An issue filed in an official repository is a report, not automatically a confirmed defect or proof that Trykatch is affected.

## Conclusion

Keep Aspire and Docker Desktop. Docker Desktop is Aspire's recommended default container runtime; Apple Silicon is not a reason to abandon this combination. Diagnose the first failing dependency instead of treating every `Waiting`, `Finished`, or `Container runtime unhealthy` message as the same problem. [Aspire prerequisites](https://aspire.dev/get-started/prerequisites/), [Aspire troubleshooting](https://aspire.dev/get-started/troubleshooting/).

The inspected template pins Aspire **13.5.3** in [Directory.Packages.props](../../templates/trykatch/Directory.Packages.props) and [the AppHost project](../../templates/trykatch/src/API/Trykatch.AppHost/Trykatch.AppHost.csproj). The affected laptop's Docker Desktop/CLI versions, active context and resource logs still need recording before assigning a root cause.

## Relevant upstream findings

| Finding | Evidence and status at review | Relevance to Trykatch |
| --- | --- | --- |
| Misleading `Container runtime unhealthy` diagnostic | Aspire **13.5.0** report, opened **2026-08-22**, remains open. Docker CLI **23.0.6** failed DCP's **25.0.0 minimum** check, but the useful version error appeared only with DCP debug logging. This is a diagnostic gap, not evidence Docker was actually stopped. [Aspire #19593](https://github.com/microsoft/aspire/issues/19593) | High: same generic message and same Aspire release family. Check the CLI used by Rider/AppHost, not just the Desktop UI version. |
| Docker startup/wake race | Docker Desktop **4.88.0**, **2026-08-24**, fixed an HTTP 500 during startup/idle wake when an engine socket existed before it was listening. [Docker release notes](https://docs.docker.com/desktop/release-notes/#4880) | A confirmed Docker defect that can resemble failed runtime detection. Relevant only if versions and logs match. |
| macOS VM crash | Docker Desktop **4.87.0**, **2026-08-17**, fixed a rare Apple Virtualization Framework VM crash immediately after a new connection. [Docker release notes](https://docs.docker.com/desktop/release-notes/#4870) | A real platform defect, not an Aspire-specific incompatibility. |
| Apple Silicon emulation startup | Docker Desktop **4.67.0**, **2026-03-30**, fixed intermittent `exec format error` for amd64 containers caused by a Rosetta/virtiofs startup race. [Docker release notes](https://docs.docker.com/desktop/release-notes/#4670) | Check the pinned image's architecture before assuming an application exception. Native arm64 images avoid unnecessary emulation. |
| Anonymous-volume argument incompatibility | Aspire report **2026-02-21** reproduced `--mount type=volume,src=,...` being rejected after a Docker update. Maintainers confirmed the DCP argument issue; **DCP #86 was closed as fixed on 2026-02-24**. Named volumes were the documented workaround. [Aspire #14603](https://github.com/microsoft/aspire/issues/14603), [DCP #86](https://github.com/microsoft/dcp/issues/86) | Our AppHost already uses named PostgreSQL and collector volumes. Do not cite this anonymous-volume bug as its cause. The issue's planned Aspire release is not, by itself, proof of the shipped fix version. |
| Slow container startup in large AppHosts | **2026-09-02**, Aspire **13.5.3**, macOS arm64: Keycloak reportedly took minutes in a large AppHost but 23 seconds in a minimal one. Open, incomplete reproduction; no established 13.5 regression. [Aspire #19858](https://github.com/microsoft/aspire/issues/19858) | Supports measuring orchestration separately from image startup. We do not use Keycloak, so this is not a diagnosis for Trykatch. |
| SQL Server volume permissions on Apple Silicon | Open report for **Aspire.Hosting.SqlServer 9.5.2**, Apple M4, **2025-11-06**; discussion includes image-version/volume compatibility and differing outcomes. [Aspire #12763](https://github.com/microsoft/aspire/issues/12763) | Not a generic named-volume defect and not PostgreSQL evidence. Do not apply destructive SQL Server workarounds to our database. |

## Expected behavior and application configuration

### Resource Saver and Docker detection

Resource Saver normally stops Docker's Linux VM after idle time and automatically wakes it when required. Docker documents roughly **3–10 seconds** of wake latency; listing images/volumes need not wake the VM. Temporarily exiting Resource Saver is a useful diagnostic, but permanently disabling it is not the default recommendation. [Docker Resource Saver](https://docs.docker.com/desktop/use-desktop/resource-saver/).

On macOS, Docker CLI uses the selected context; Desktop sets `desktop-linux` on startup. Other clients may need the optional `/var/run/docker.sock` symlink or an explicitly configured socket. `DOCKER_HOST`, `DOCKER_CONTEXT`, and command-line flags can override the expected destination. An IDE with a different `PATH`/environment can therefore behave differently from a terminal; that is a configuration hypothesis to verify. [Docker macOS permissions and socket setup](https://docs.docker.com/desktop/setup/install/mac-permission-requirements/), [Docker contexts](https://docs.docker.com/engine/manage-resources/contexts/).

An older official Aspire investigation traced runtime detection failure to slow `docker info`, predominantly in Windows/WSL reports. It resulted in a forced-runtime workaround; it does **not** establish a current macOS defect. Use current `ASPIRE_CONTAINER_RUNTIME` configuration rather than blindly copying historical environment-variable names. [Aspire #7802](https://github.com/microsoft/aspire/issues/7802), [current AppHost configuration](https://aspire.dev/app-host/configuration/).

### Startup dependencies and fixed ports

The current [AppHost](../../templates/trykatch/src/API/Trykatch.AppHost/Program.cs) intentionally waits in this order: database → migrator → API → web. `WaitForCompletion(migrator)` expects a completed process with exit code **0**. A successful migrator should be `Finished`, not run forever. If it fails, downstream waiting is correct fail-closed behavior; inspect PostgreSQL and migrator logs first. [Aspire WaitForCompletion API](https://aspire.dev/reference/api/csharp/aspire.hosting/resourcebuilderextensions/methods/).

The AppHost's `targetPort: 3000/3100/3200/4318/9090` values are **container listener ports**, not fixed host-port reservations. Reusing a target port in isolated containers is normal. Explicit host `port:` settings, launch-profile ports and another running AppHost can collide; Aspire recommends omitting host ports where stable ports are unnecessary. [Aspire networking](https://aspire.dev/fundamentals/networking-overview/).

There was also a separate isolated-AppHost resource-service port collision fixed in Aspire's 13.5 changes. Do not confuse that upstream bug with intentionally binding two applications to the same fixed host port. [Aspire 13.5 change log](https://github.com/microsoft/aspire/wiki/13.5-Change-log).

### Named volumes and permissions

The review initially found fixed volume names `app-postgres-data` and `app-otel-queue`, which allowed different generated projects on the same Docker daemon to reuse storage. The template now derives both names from the generated application slug—for example, `horizon-postgres-data` and `horizon-otel-queue`—and its packaging check enforces that contract. Named volumes still outlive their containers, so an already-generated application retains its existing volumes until its operator performs a deliberate, application-specific migration or cleanup. [Docker volume lifecycle](https://docs.docker.com/engine/storage/volumes/).

The [collector image](../../templates/trykatch/deploy/observability/otel-collector.Dockerfile) seeds queue directories owned by UID/GID `10001`. Docker copies image contents into an **empty** volume, but an existing non-empty volume hides those image contents. Consequently, rebuilding the image does not itself repair old queue-directory ownership. This is a storage/configuration inference from the code and Docker's mount semantics, not an Aspire bug. Inspect the exact mount and container user; back up data before a targeted repair. Do not run broad volume pruning or disable container isolation to suppress the error. [Docker mount semantics](https://docs.docker.com/engine/storage/volumes/#mounting-a-volume-over-existing-data).

## Recommended verification order

Run these read-only diagnostics in the same terminal/environment used to launch the AppHost; do not paste credentials or complete environment dumps into public issues:

```sh
uname -m
command -v docker
docker version
docker context show
docker context ls
docker info --format '{{.ServerVersion}} {{.OSType}} {{.Architecture}}'
aspire doctor
docker ps -a --format 'table {{.Names}}\t{{.Status}}\t{{.Ports}}'
```

`aspire doctor` reports SDK, active runtime, runtime versions and environment checks. If unavailable because the optional Aspire CLI is not installed, collect the Docker checks and AppHost logs instead. For the generic runtime error, rerun the normal AppHost command with `Logging__LogLevel__Aspire.Hosting.Dcp=Debug` and inspect the specific failure. [Aspire doctor](https://aspire.dev/reference/cli/commands/aspire-doctor/), [diagnostic example](https://github.com/microsoft/aspire/issues/19593).

Next, inspect logs for the **first** failed resource, verify its pinned image supports the machine architecture, and compare its actual published ports/mounts with the model. Test one cold start, one warm start, and one start after Docker idle wake. A green unit/integration suite does not replace this generated-AppHost smoke test. No product code, Docker settings, volumes, or running services were changed by this research.
