# Agents Guide

Soulman is a .NET background service that watches a Soulseek downloads folder (e.g. `Documents/Soulseek Downloads/complete/<username>`) and moves completed music into a chosen library after reading ID3 tags.

Follow these rules whenever you change behavior:

1. Keep docs in lockstep
   - Update `README.md` with prerequisites, install/run steps (Windows service, CLI), and any new defaults for source/destination selection or schedules.
   - If you add flags, config keys, or UI controls, document them where users configure things (examples, screenshots, or `docs/` pages).

2. Describe the pipeline
   - When changing how we detect finished downloads, parse ID3 tags, or decide when to move/rename files, record the flow and states so others know how "complete" is determined and what happens on errors.

3. User controls and schedules
   - Any new knobs for polling intervals, folder selection, retries, conflict resolution, or tag override rules must be captured in usage docs and sample configs; note sensible defaults.

4. Packaging and platform notes
   - Clarify Windows-specific pieces (service install, tray app, filesystem watcher quirks) and call out any cross-platform differences if we add them.

5. Validation and observability
   - Keep tests/logging aligned with behavior: add coverage or smoke scripts for the watcher loop and tagging, and make sure logs clearly show source, destination, and actions users can troubleshoot.

Documentation staying truthful is part of done; don't ship behavior without updating how it's explained.
