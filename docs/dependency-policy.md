# Dependency policy

Flatpack uses redistributable open-source libraries. New dependencies must pass vulnerability review and use an approved SPDX license: Apache-2.0, MIT, BSD-2-Clause, BSD-3-Clause, ISC, PostgreSQL, MS-PL, Zlib, or Unlicense.

The Grafana, Loki, and Tempo services are separately deployed observability infrastructure; their licenses do not apply to Flatpack's source through linking. Review their current terms before offering them as a managed service.

Pull requests are checked by GitHub dependency review. Release CI also audits NuGet and pnpm dependencies, scans secrets, and emits an SPDX JSON SBOM.
