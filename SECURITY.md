# Security Policy

## Supported Versions

`wgfetch` is pre-1.0 and does not yet publish tagged releases. Security fixes are applied to the
`main` branch only; there is no backport policy until a first stable release ships.

## Reporting a Vulnerability

Please **do not** open a public GitHub issue for security vulnerabilities.

Instead, use GitHub's private vulnerability reporting for this repository:
[Report a vulnerability](https://github.com/dennispayne/astrostack-downloader/security/advisories/new)

Include, where possible:

- A description of the vulnerability and its impact.
- Steps to reproduce, or a minimal proof of concept.
- The affected commit or branch.

You should expect an initial response within a few days. If the report is accepted, a fix will be
developed privately and disclosed alongside a security advisory once available.

## Scope

`wgfetch` downloads third-party installers from the open internet on the user's behalf. Reports
about the following are especially relevant:

- Bypasses of the domain allowlist, redirect policy, or magic-byte/content verification gate.
- Cases where an unverified file (login page, error page, interstitial) could be written to disk
  or accepted as a legitimate installer.
- Secret or credential leakage in logs, diagnostics bundles, or error output.
- Prompt injection via fetched web content that influences a download decision.

This project treats local LLM inference as untrusted input: a model may *propose* a candidate URL,
but must never be able to *authorize* a download past the mechanical verification gate. Reports
demonstrating a path around that gate are treated as high severity.
