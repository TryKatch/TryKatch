# Dependency policy

This generated application starts with redistributable open-source libraries. New dependencies must pass vulnerability review and use an approved SPDX license: Apache-2.0, MIT, BSD-2-Clause, BSD-3-Clause, ISC, PostgreSQL, MS-PL, Zlib, or Unlicense.

Grafana, Loki, and Tempo are separately deployed infrastructure. Review their current terms before offering them as a managed service.

CI audits NuGet and pnpm dependencies, scans secrets, and emits an SPDX JSON SBOM.
