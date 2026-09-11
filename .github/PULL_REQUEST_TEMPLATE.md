<!-- Thanks for contributing! Please read CONTRIBUTING.md before opening this pull request. -->

## Summary

<!-- What does this change do, and why? -->

Closes #

## Specification

<!-- docs/REQUIREMENTS.md is authoritative. Tick one. -->

- [ ] This change is consistent with `docs/REQUIREMENTS.md` as written.
- [ ] This change intentionally diverges from the spec, and updates `docs/REQUIREMENTS.md` in this
      same pull request (explain why below).

## Checklist

- [ ] `dotnet build wgfetch.slnx -c Release` passes.
- [ ] The hermetic suite passes with no network access and no model files present:
      `dotnet test wgfetch.slnx -c Release --filter "Category!=Live&Category!=Weights&Category!=Aot"`.
- [ ] `dotnet format wgfetch.slnx` leaves no changes.
- [ ] New or changed behaviour is covered by tests; no test reaches the network outside the
      `Category=Live` tier. Verification-gate, resume and version-comparator changes include
      adversarial and property-based tests.
- [ ] No telemetry, analytics, update checks or phone-home added; no secrets logged or committed.
- [ ] Installer bytes are untouched — no installer is extracted, repacked or rewritten; manifest
      metadata rewrites are limited to permitted fields.
- [ ] Docs affected by this change are updated.

## Verification gate

<!-- Delete this section if the change does not touch Verification/, Downloads/ or Versioning/. -->

- [ ] Adversarial and property-based tests were added (a model or upstream response cannot move
      an unverified URL past the gate).
- [ ] Mutation testing still passes:
      `dotnet tool run dotnet-stryker -- --config-file stryker-config.json`.
