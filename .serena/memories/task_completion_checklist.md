# Completion checklist

Before claiming a change is complete:
- Keep stdout contract intact: commands emit only one JSON result to stdout; all progress stays on stderr.
- For sidecar changes, run at least `dotnet build EtabExtension.CLI.slnx` or the specific project build.
- For Rust integration changes in `D:/Work/EtabExtension`, run focused crate tests first, then broader checks when risk warrants it.
- If ETABS behavior changes, verify whether live ETABS is required; live tests are opt-in and need local ETABS plus environment configuration.
- If output contracts change, update shared Rust/TypeScript contract types and CLI integration tests in the main repo.
- Do not commit `.serena/` unless the user explicitly wants project memory tracked.