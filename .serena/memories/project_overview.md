# EtabExtension.CLI overview

Purpose: C#/.NET sidecar CLI for automating ETABS from the main Rust/Tauri EtabExtension app. It exposes single-shot commands that return exactly one JSON object on stdout and send progress/debug output to stderr.

Tech stack: .NET 10 executable, C# nullable enabled, System.CommandLine for CLI surface, Microsoft.Extensions.Hosting/DI, EtabSharp for ETABS COM automation, Parquet.Net for table exports.

Structure:
- `src/EtabExtension.CLI/Program.cs`: host setup, DI registration, root command registration, stdout redirection to stderr.
- `Features/*`: command/service pairs for `status`, `open-model`, `close-model`, `unlock-model`, `generate-e2k`, `extract-materials`, `run-analysis`, `extract-results`.
- `Shared/Common`: `Result` envelopes and JSON stdout helpers.
- `Shared/Infrastructure/Etabs`: ETABS unit and table query/edit infrastructure.
- `Shared/Infrastructure/Parquet`: dynamic all-string parquet writer for ETABS table data.
- `EtabExtension.CLI.VisualTest`: manual/visual test harness.

Integration boundary: designed to be called by sibling repo `D:/Work/EtabExtension`, especially Rust crates `ext-core` sidecar client/types and `ext-api` ETABS workflows.