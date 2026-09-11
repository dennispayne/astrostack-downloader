# astrostack-dsc interop

The primary consumer is [astrostack-dsc](https://github.com/dennispayne/astrostack-dsc).

```powershell
wgfetch import --format astrostack-dsc <dsc-repo>\manifest\components   # seed targets.yaml
wgfetch export --format astrostack-dsc --out .\patches                  # emit machine-owned fields
```

**Field ownership is the governing rule.** `wgfetch` owns exactly `downloadUrl`, `downloadFileName`,
`sha256`, `verified` and `availableVersion`. Every other field — `expectedVersion`, `checkPath`,
`versionSource`, `versionStrategy`, `versionRegex`, `silentInstallArgs`, `archiveContainsInstaller`,
`module`, `kind`, `notes`, `installNotes` — is human-owned and is **never** written.

In particular `expectedVersion` is **never auto-bumped**: it is a deliberate human pin that gates DSC
remediation. Newer builds are reported in `availableVersion` for a person to promote. Silently
advancing a pin would turn an audit tool into an uncontrolled auto-updater.

Exports are **per-component partial patches**, never whole-file replacements, so hand-written `notes`
survive. The export also contains a `path-mapping.json` so the DSC side resolves artifacts by ID
rather than guessing filenames — `installers/` keeps its winget-shaped layout, which is the contract
for the other three consumption paths (see [`docs/USAGE.md`](USAGE.md)).

Archives are not extracted. `nina.json` sets `archiveContainsInstaller: true` because the vendor
ships a ZIP bundle; `wgfetch` fetches that ZIP byte-for-byte and records `NestedInstallerType` and
`NestedInstallerFiles` in the winget manifest. Unpacking is the consumer's job.
