# CLAUDE.md

Coding preferences for this repo. The spec is `docs/specs/Technical Assessment.pdf`.

## Working style
- Work one step at a time: briefly explain the next step, then wait for approval.
- Don't present long multi-step roadmaps; focus on the next step only.

## General
- Use existing libraries when it's an easy option; don't roll your own.
- Keep comments short: about 10–20 words per point. Longer rationale goes in the README.
- Never put credentials in committed files. Docker Compose reads them from `.env`; local runs read gitignored `appsettings.Development.json`.

## C#
- Always use braces for `if`, `else`, loops, etc., even for one-line bodies.
- Async all the way down: async APIs, pass `CancellationToken`s, never block with `.Result`/`.Wait()`.
- Set the .NET version with `<TargetFramework>` in each project; no `global.json`.
- All tests live in one project: `tests/Crawler.Tests`.
