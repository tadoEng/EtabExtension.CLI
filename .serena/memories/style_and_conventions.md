# Style and conventions

- C# with nullable enabled and implicit usings.
- Feature folders follow `FeatureNameCommand`, `FeatureNameService`, interface, extensions, and models.
- Services usually return `Task<Result<T>>`; failures use `Result.Fail<T>(...)`, success uses `Result.Ok(...)`.
- Command handlers should call `ExitWithResult()` so stdout remains parseable JSON.
- `Program.cs` redirects `Console.Out` to stderr at startup; write actual command result JSON only through `JsonExtensions.WriteJsonToStdout` / `ExitWithResult`.
- Progress/debug logs go to `Console.Error` or ILogger.
- ETABS commands generally fail fast on file/path/unit validation before starting ETABS.
- Hidden ETABS extraction/analysis commands call `ETABSWrapper.CreateNew()`, hide the app, open the `.edb`, do work, then `ApplicationExit(false)` and `Dispose()` in `finally`.
- User-visible open command may connect to an existing ETABS instance or create a visible new instance; it should not accidentally terminate user-controlled ETABS.
- Table extraction uses all-string parquet columns because ETABS database tables return strings.