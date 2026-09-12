# Instructions for AI coding agents

These instructions apply to any AI agent (including GitHub Copilot cloud agent) working in this
repository — read them before making changes.

## Always add a regression test with a bug fix

When your task is to fix a bug (an issue that describes broken/incorrect/crashing behavior):

1. **Write a test that fails before your fix and passes after it.** The test must exercise the
   exact scenario from the bug report (same command, same inputs, same edge case) closely enough
   that reverting your fix would make the test fail again.
2. **Put the test in the existing hermetic suite** (`WgFetch.Core.Tests`, `Category!=Live` and
   `Category!=Weights` and `Category!=Aot`) unless the bug can only be reproduced with real network
   access or real model weights — in that case, tag it with the matching `Category` trait and
   explain why in a comment.
3. **Name the test after the behavior, not the issue number** (e.g.
   `LoadAsync_MalformedYaml_ThrowsFriendlyError`, not `Issue14Test`), but reference the issue number
   in a `//` comment or the test's summary so the link is traceable.
4. Run `dotnet test wgfetch.slnx -c Release --filter "Category!=Live&Category!=Weights&Category!=Aot"`
   locally (or via CI) and confirm it's green before marking the PR ready for review. See
   [`docs/DEVELOPMENT.md`](../docs/DEVELOPMENT.md) for the full test-tier breakdown.

This applies even to small or "obvious" fixes — a one-line fix with no test is still an unfinished
fix here, because the whole point is to make sure the same regression can't silently come back.

## Feature work

For new features (not bug fixes), add tests that cover the new behavior's success path and its
realistic failure/edge cases (invalid input, missing files, empty state, etc.) — follow the existing
patterns in `WgFetch.Core.Tests` for style and fixture setup.

## Before opening/updating a PR

- Run the hermetic test filter above.
- Run `dotnet format --verify-no-changes` (or let `lint.yml` catch it) — don't hand-format.
- Keep changes scoped to the linked issue; don't opportunistically refactor unrelated code.
