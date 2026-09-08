# Flatpack CLI

The Flatpack CLI validates and composes the backend and React surfaces of a generated Flatpack application from one authoritative `flatpack.modules.json` catalog.

```bash
dotnet tool install --global Flatpack.Cli --prerelease
flatpack module doctor --root /path/to/application
```

Use `module list`, `module generate`, `module enable <id>`, and `module disable <id>` to inspect and change the installed module graph. Enable and disable operations use atomic file replacement with rollback on failure and never remove module data.

Package acquisition, upgrade, eject, unregister, and purge-data operations are intentionally not part of this preview.
