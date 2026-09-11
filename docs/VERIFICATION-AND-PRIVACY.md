# Verification and privacy

## The verification gate

A local language model participates in URL discovery, so it is treated as **unreliable and
adversarial by default**. It may *propose* candidate URLs and rank page content; it may never
*authorize* a download.

Every candidate — from a recipe, from GitHub, from winget, or from the model — passes the same
purely mechanical gate before a single byte is retained:

1. Host matches an **allowlisted vendor domain** for the resolved package.
2. **HTTPS only**; redirects are followed only within the allowlist, and a redirect off-allowlist is
   a hard failure.
3. A `HEAD` or ranged `GET` succeeds.
4. `Content-Type` indicates a binary payload, not `text/html`.
5. **Magic bytes** of the leading chunk match a known installer format (PE/MZ, MSI/OLE compound,
   ZIP/MSIX, CAB, 7z, Inno, NSIS).
6. Content length is plausible for an installer and inconsistent with an error or interstitial page.

Anything failing is rejected and logged with the exact check that rejected it. **A login page, error
page or interstitial is never written to disk and never hashed as an installer.** No model output
can move a URL past this gate.

Downloads are resumable but never spliced: a resume requires an `ETag`/`Last-Modified` validator and
a `206` response, a `200` answer to a ranged request restarts from zero, and the final path only ever
appears after full-length and SHA256 verification via an atomic rename.

Mutation testing (Stryker.NET) is enforced in CI specifically over this gate, the download/
atomic-rename path and the version comparator — see [`docs/DEVELOPMENT.md`](DEVELOPMENT.md).

## Privacy: every network destination

`wgfetch` has **no telemetry, no analytics, no crash reporting, no update checks and no phone-home
of any kind.** The complete list of hosts it can contact, and why:

| Destination | When | How to avoid it |
| --- | --- | --- |
| `huggingface.co`, and the CDN host it redirects large model files to (currently `us.aws.cdn.hf.co`) | `wgfetch prereqs install`, or `--download-prereqs` | Fetch the pinned models manually and point `--models-root` at them |
| The configured search provider | Only during LLM-assisted discovery | `--search-provider none` |
| `api.github.com` | Resolving GitHub-hosted releases | Not contacted when no target resolves via GitHub |
| Allowlisted vendor download hosts | Verification and download | Unavoidable; this is the product |
| Your own `--ai-endpoint` | Only under `--ai-mode remote` or `auto` with a remote configured | Default `--ai-mode local` never leaves the machine |

Inference is **offline-local by default** and runs on-box against the pinned ONNX models. The
optional OpenAI-compatible remote endpoint is opt-in only and never a silent fallback.

API keys and tokens are redacted from logs at every verbosity, from `provenance.json`, from `--json`
output and from `wgfetch diagnostics` bundles. Nothing is ever written to disk in cleartext by
`wgfetch` that it did not receive from you.

See also [`SECURITY.md`](../SECURITY.md) for the vulnerability reporting process and this project's
threat model.
