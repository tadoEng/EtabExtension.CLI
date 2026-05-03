# Suggested commands

Windows/PowerShell utilities:
- `Get-ChildItem -Recurse -File` as fallback file listing when `rg` is unavailable.
- `Select-String -Path ... -Pattern ...` for text search.
- `git status --short --branch`
- `git ls-files`

Project commands:
- `dotnet build EtabExtension.CLI.slnx`
- `dotnet build src/EtabExtension.CLI/EtabExtension.CLI.csproj`
- `dotnet run --project src/EtabExtension.CLI/EtabExtension.CLI.csproj -- --help`
- `dotnet run --project src/EtabExtension.CLI/EtabExtension.CLI.csproj -- status`
- `dotnet run --project src/EtabExtension.CLI/EtabExtension.CLI.csproj -- extract-results --help`
- `./build-sidecar.ps1` to publish/build the sidecar for integration.

Siblings/integration:
- Main app repo is `D:/Work/EtabExtension`.
- Relevant Rust commands there include `cargo test -p ext-api etabs_api`, `cargo test -p ext etabs_cli`, `cargo test --workspace`, `cargo fmt --check --all`, and `cargo clippy --workspace --all-targets`.