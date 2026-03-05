// ╔══════════════════════════════════════════════════════════════════════════╗
// ║         EtabExtension.CLI.VisualTest · Program.cs                       ║
// ║                                                                          ║
// ║  Interactive TUI that calls real service classes IN-PROCESS.            ║
// ║  Just press F5 — no CLI binary, no env vars, no extra setup.           ║
// ║                                                                          ║
// ║  Requires: project reference to EtabExtension.CLI                       ║
// ╚══════════════════════════════════════════════════════════════════════════╝

using System.Text;
using System.Text.Json;
using EtabExtension.CLI.Features.CloseModel;
using EtabExtension.CLI.Features.ExtractMaterials;
using EtabExtension.CLI.Features.ExtractResults;
using EtabExtension.CLI.Features.ExtractResults.Models;
using EtabExtension.CLI.Features.GenerateE2K;
using EtabExtension.CLI.Features.GetStatus;
using EtabExtension.CLI.Features.OpenModel;
using EtabExtension.CLI.Features.RunAnalysis;
using EtabExtension.CLI.Features.UnlockModel;
using EtabExtension.CLI.Shared.Common;
using EtabExtension.CLI.Shared.Infrastructure.Etabs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

Console.OutputEncoding = Encoding.UTF8;
Console.Title = "etab-cli · Visual Test";

// Enable ANSI on Windows terminal
if (OperatingSystem.IsWindows())
{
    var h = GetStdHandle(-11);
    GetConsoleMode(h, out var m);
    SetConsoleMode(h, m | 0x0004);
}

// ── Build the same DI container as the real CLI ───────────────────────────────
var sp = new ServiceCollection()
    .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
    .AddEtabsInfrastructure()
    .AddGetStatusFeature()
    .AddOpenModelFeature()
    .AddCloseModelFeature()
    .AddUnlockModelFeature()
    .AddGenerateE2KFeature()
    .AddExtractMaterialsFeature()
    .AddRunAnalysisFeature()
    .AddExtractResultsFeature()
    .BuildServiceProvider();

await Tui.RunAsync(sp);

// ── P/Invoke: enable ANSI on Windows ─────────────────────────────────────────
[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern nint GetStdHandle(int n);
[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetConsoleMode(nint h, out uint m);
[System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool SetConsoleMode(nint h, uint m);

// ═══════════════════════════════════════════════════════════════════════════════
// ANSI PALETTE  — amber-phosphor CRT theme
// ═══════════════════════════════════════════════════════════════════════════════
static class C
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Gold = "\x1b[38;2;255;180;0m";
    public const string Amber = "\x1b[38;2;210;120;0m";
    public const string Dim = "\x1b[38;2;120;70;0m";
    public const string Green = "\x1b[38;2;80;220;120m";
    public const string Red = "\x1b[38;2;255;80;60m";
    public const string Cyan = "\x1b[38;2;80;200;220m";
    public const string White = "\x1b[38;2;230;220;200m";
    public const string Faint = "\x1b[38;2;100;90;70m";
    public const string BgSel = "\x1b[48;2;60;35;0m";
}

// ═══════════════════════════════════════════════════════════════════════════════
// SESSION STATE  — persisted between menu visits
// ═══════════════════════════════════════════════════════════════════════════════
static class S
{
    public static string EdbPath = string.Empty;
    public static string OutputDir = string.Empty;
}

// ═══════════════════════════════════════════════════════════════════════════════
// TUI
// ═══════════════════════════════════════════════════════════════════════════════
static class Tui
{
    static int W => Math.Min(Console.WindowWidth > 0 ? Console.WindowWidth - 1 : 110, 120);

    static readonly (string Name, string Hint)[] Menu =
    [
        ("get-status",         "Is ETABS running? PID, version, open file, units"),
        ("open-model",         "Open an .edb in the running ETABS (Mode A)"),
        ("close-model",        "Clear workspace, ETABS keeps running"),
        ("unlock-model",       "Clear post-analysis lock on current file"),
        ("generate-e2k",       "Export .edb → .e2k text format  [Mode B]"),
        ("extract-materials",  "Material takeoff → .parquet       [Mode B]"),
        ("run-analysis",       "Run analysis, save results         [Mode B]"),
        ("extract-results",    "All results tables → .parquet      [Mode B]"),
    ];

    public static async Task RunAsync(IServiceProvider sp)
    {
        int sel = 0;

        while (true)
        {
            // ── draw header + menu ────────────────────────────────────────────
            DrawHeader();
            Ln($" {C.Gold}{C.Bold}COMMANDS{C.Reset}  {C.Faint}↑ ↓ to move  ·  Enter to run  ·  Q to quit{C.Reset}\n");

            for (int i = 0; i < Menu.Length; i++)
            {
                var (name, hint) = Menu[i];
                if (i == sel)
                    Ln($"  {C.BgSel}{C.Gold} › {name,-22}{C.Reset}  {C.Amber}{hint}{C.Reset}");
                else
                    Ln($"    {C.Amber}{name,-22}{C.Reset}  {C.Faint}{hint}{C.Reset}");
            }

            // ── input ─────────────────────────────────────────────────────────
            var k = Console.ReadKey(true);

            if (k.Key == ConsoleKey.UpArrow) { sel = (sel - 1 + Menu.Length) % Menu.Length; continue; }
            if (k.Key == ConsoleKey.DownArrow) { sel = (sel + 1) % Menu.Length; continue; }
            if (k.Key is ConsoleKey.Q or ConsoleKey.Escape) break;
            if (k.Key != ConsoleKey.Enter) continue;

            // ── dispatch ──────────────────────────────────────────────────────
            DrawHeader();
            Ln($" {C.Bold}{C.Gold}{Menu[sel].Name}{C.Reset}  {C.Faint}{Menu[sel].Hint}{C.Reset}");
            Ln($"{C.Dim}{new string('─', W)}{C.Reset}\n");

            switch (sel)
            {
                case 0: await Do_GetStatus(sp); break;
                case 1: await Do_OpenModel(sp); break;
                case 2: await Do_CloseModel(sp); break;
                case 3: await Do_UnlockModel(sp); break;
                case 4: await Do_GenerateE2K(sp); break;
                case 5: await Do_ExtractMaterials(sp); break;
                case 6: await Do_RunAnalysis(sp); break;
                case 7: await Do_ExtractResults(sp); break;
            }

            Ln($"\n{C.Faint}Press any key to return to menu…{C.Reset}");
            Console.ReadKey(true);
        }

        Console.Clear();
        Ln($"{C.Amber}Goodbye.{C.Reset}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // COMMAND IMPLEMENTATIONS  — each resolves its service, builds args, invokes
    // ═══════════════════════════════════════════════════════════════════════════

    static async Task Do_GetStatus(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IGetStatusService>();
        await Invoke(() => svc.GetStatusAsync());
    }

    static async Task Do_OpenModel(IServiceProvider sp)
    {
        var edb = AskPath("EDB file", S.EdbPath);
        if (edb is null) return;
        S.EdbPath = edb;

        var save = AskBool("Save current model first?", false);
        var newInst = AskBool("Open in a new ETABS instance?", false);

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IOpenModelService>();
        await Invoke(() => svc.OpenModelAsync(edb, save, newInst));
    }

    static async Task Do_CloseModel(IServiceProvider sp)
    {
        var save = AskBool("Save before clearing?", false);

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<ICloseModelService>();
        await Invoke(() => svc.CloseModelAsync(save));
    }

    static async Task Do_UnlockModel(IServiceProvider sp)
    {
        var edb = AskPath("EDB file (must be open in ETABS)", S.EdbPath);
        if (edb is null) return;
        S.EdbPath = edb;

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IUnlockModelService>();
        await Invoke(() => svc.UnlockModelAsync(edb));
    }

    static async Task Do_GenerateE2K(IServiceProvider sp)
    {
        var edb = AskPath("EDB file", S.EdbPath);
        if (edb is null) return;
        S.EdbPath = edb;

        var outFile = AskStr("Output .e2k file", Path.ChangeExtension(edb, ".e2k"));
        var over = AskBool("Overwrite if exists?", true);

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IGenerateE2KService>();
        await Invoke(() => svc.GenerateE2KAsync(edb, outFile, over));
    }

    static async Task Do_ExtractMaterials(IServiceProvider sp)
    {
        var edb = AskPath("EDB file", S.EdbPath);
        if (edb is null) return;
        S.EdbPath = edb;

        var outDir = AskStr("Output directory", S.OutputDir);
        if (!string.IsNullOrEmpty(outDir)) S.OutputDir = outDir;

        var tableKey = AskStr("Table key", "Material List by Story");

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExtractMaterialsService>();
        await Invoke(() => svc.ExtractMaterialsAsync(edb, outDir, tableKey));
    }

    static async Task Do_RunAnalysis(IServiceProvider sp)
    {
        var edb = AskPath("EDB file", S.EdbPath);
        if (edb is null) return;
        S.EdbPath = edb;

        var raw = AskStr("Load cases (comma-sep, blank = ALL)", string.Empty);
        List<string>? cases = null;
        if (!string.IsNullOrWhiteSpace(raw))
            cases = [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        Ln($"\n{C.Amber}⚠  This will run ETABS analysis — may take several minutes.{C.Reset}");
        if (!AskBool("Continue?", true)) return;

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IRunAnalysisService>();
        await Invoke(() => svc.RunAnalysisAsync(edb, cases));
    }

    static async Task Do_ExtractResults(IServiceProvider sp)
    {
        var edb = AskPath("EDB file", S.EdbPath);
        if (edb is null) return;
        S.EdbPath = edb;

        var outDir = AskStr("Output directory", S.OutputDir);
        if (!string.IsNullOrEmpty(outDir)) S.OutputDir = outDir;

        // ── Table picker (Space to toggle, Enter to confirm) ──────────────────
        Ln($"\n {C.Cyan}Select tables:{C.Reset}  {C.Faint}Space = toggle  ·  Enter = confirm{C.Reset}\n");

        var rows = new[]
        {
            (Key: "storyDefinitions",      Label: "Story Definitions",       On: true),
            (Key: "baseReactions",         Label: "Base Reactions",          On: true),
            (Key: "storyForces",           Label: "Story Forces",            On: true),
            (Key: "jointDrifts",           Label: "Joint Drifts",            On: true),
            (Key: "pierForces",            Label: "Pier Forces",             On: true),
            (Key: "pierSectionProperties", Label: "Pier Section Properties", On: true),
        };
        var on = rows.Select(r => r.On).ToArray();
        int cur = 0, top = Console.CursorTop;

        while (true)
        {
            Console.SetCursorPosition(0, top);
            for (int i = 0; i < rows.Length; i++)
            {
                var chk = on[i] ? $"{C.Green}[✓]{C.Reset}" : $"{C.Dim}[ ]{C.Reset}";
                var lbl = i == cur
                    ? $"{C.BgSel}{C.Gold} {rows[i].Label,-30}{C.Reset}"
                    : $"{C.White} {rows[i].Label,-30}{C.Reset}";
                Ln($"   {chk} {lbl}");
            }
            var k = Console.ReadKey(true);
            if (k.Key == ConsoleKey.UpArrow) cur = (cur - 1 + rows.Length) % rows.Length;
            else if (k.Key == ConsoleKey.DownArrow) cur = (cur + 1) % rows.Length;
            else if (k.Key == ConsoleKey.Spacebar) on[cur] = !on[cur];
            else if (k.Key is ConsoleKey.Enter or ConsoleKey.Escape) break;
        }

        Ln();

        // ── Per-table filter prompts ──────────────────────────────────────────
        var selections = new TableSelections();

        for (int i = 0; i < rows.Length; i++)
        {
            if (!on[i]) continue;

            var key = rows[i].Key;
            var label = rows[i].Label;

            Ln($" {C.Amber}▸ {label}{C.Reset}");

            string[]? cases = null;
            string[]? combos = null;
            string[]? groups = null;

            if (key is "baseReactions" or "storyForces" or "jointDrifts")
            {
                var raw = AskStr("  Load cases (comma-sep, blank = all)", string.Empty);
                if (!string.IsNullOrWhiteSpace(raw))
                    cases = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (key is "pierForces")
            {
                var raw = AskStr("  Load combos (comma-sep, blank = all)", string.Empty);
                if (!string.IsNullOrWhiteSpace(raw))
                    combos = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            if (key is "jointDrifts" or "pierForces" or "pierSectionProperties")
            {
                var raw = AskStr("  ETABS groups (comma-sep, blank = whole model)", string.Empty);
                if (!string.IsNullOrWhiteSpace(raw))
                    groups = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            var filter = new TableFilter
            {
                LoadCases = cases,
                LoadCombos = combos,
                Groups = groups
            };

            selections = key switch
            {
                "storyDefinitions" => selections with { StoryDefinitions = filter },
                "baseReactions" => selections with { BaseReactions = filter },
                "storyForces" => selections with { StoryForces = filter },
                "jointDrifts" => selections with { JointDrifts = filter },
                "pierForces" => selections with { PierForces = filter },
                "pierSectionProperties" => selections with { PierSectionProperties = filter },
                _ => selections
            };

            Ln();
        }

        var request = new ExtractResultsRequest
        {
            FilePath = edb,
            OutputDir = outDir,
            Tables = selections
        };

        using var scope = sp.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IExtractResultsService>();
        await Invoke(() => svc.ExtractAsync(request));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // INVOKE WRAPPER
    // ═══════════════════════════════════════════════════════════════════════════
    // The services write progress to Console.Error (✓ ℹ ⚠ lines).
    // We intercept those and re-print them with color before showing the JSON.

    static async Task Invoke<T>(Func<Task<Result<T>>> call)
    {
        var origErr = Console.Error;
        var errWriter = new ColorStderrWriter(origErr);
        Console.SetError(errWriter);

        Ln($"{C.Dim}{new string('─', W)}{C.Reset}");

        Result<T>? result = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            result = await call();
        }
        catch (Exception ex)
        {
            Ln($"{C.Red}✗ Unhandled exception: {ex.GetType().Name}: {ex.Message}{C.Reset}");
        }
        finally
        {
            Console.SetError(origErr);
            sw.Stop();
        }

        if (result is null) return;

        Ln($"{C.Dim}{new string('─', W)}{C.Reset}");

        // Pretty-print the JSON result with simple syntax highlighting
        var json = JsonSerializer.Serialize(result,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

        foreach (var raw in json.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains(": true")) Ln($"  {C.Green}{line}{C.Reset}");
            else if (line.Contains(": false")) Ln($"  {C.Red}{line}{C.Reset}");
            else if (line.Contains(": null")) Ln($"  {C.Faint}{line}{C.Reset}");
            else if (line.TrimStart().StartsWith('"') && line.Contains(':'))
            {
                var col = line.IndexOf(':');
                Ln($"  {C.Amber}{line[..col]}{C.Reset}{C.Cyan}{line[col..]}{C.Reset}");
            }
            else Ln($"  {C.Faint}{line}{C.Reset}");
        }

        Ln($"{C.Dim}{new string('─', W)}{C.Reset}");

        if (result.Success)
            Ln($"\n  {C.Green}{C.Bold}✓ SUCCESS{C.Reset}  {C.Faint}{sw.Elapsed.TotalSeconds:F1}s{C.Reset}");
        else
            Ln($"\n  {C.Red}{C.Bold}✗ FAILED{C.Reset}   {C.Faint}{sw.Elapsed.TotalSeconds:F1}s{C.Reset}  {C.Red}{result.Error}{C.Reset}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // INPUT HELPERS
    // ═══════════════════════════════════════════════════════════════════════════

    static string? AskPath(string label, string def)
    {
        while (true)
        {
            var v = AskStr(label, def);
            if (string.IsNullOrWhiteSpace(v)) { Ln($"  {C.Faint}(skipped){C.Reset}"); return null; }
            if (File.Exists(v)) return v;
            Ln($"  {C.Red}✗ File not found: {v}{C.Reset}");
            if (!AskBool("  Try again?", true)) return null;
        }
    }

    static string AskStr(string label, string def)
    {
        var hint = string.IsNullOrEmpty(def) ? "" : $" [{C.Dim}{def}{C.Reset}]";
        Console.Write($"  {C.Cyan}{label}{C.Reset}{hint} {C.Gold}›{C.Reset} ");
        var v = Console.ReadLine()?.Trim() ?? "";
        return string.IsNullOrEmpty(v) ? def : v;
    }

    static bool AskBool(string label, bool def)
    {
        var hint = def ? $"[{C.Green}Y{C.Reset}/{C.Dim}n{C.Reset}]" : $"[{C.Dim}y{C.Reset}/{C.Red}N{C.Reset}]";
        Console.Write($"  {C.Cyan}{label}{C.Reset} {hint} {C.Gold}›{C.Reset} ");
        return Console.ReadLine()?.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" => true,
            "n" or "no" => false,
            _ => def
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RENDERING
    // ═══════════════════════════════════════════════════════════════════════════

    static void DrawHeader()
    {
        Console.Clear();
        var title = " etab-cli · Visual Test ";
        var inner = W - 2;
        var pad = (inner - title.Length) / 2;
        Ln($"{C.Gold}╔{new string('═', inner)}╗{C.Reset}");
        Ln($"{C.Gold}║{new string(' ', pad)}{C.Bold}{C.Gold}{title}{C.Reset}{C.Gold}{new string(' ', inner - pad - title.Length)}║{C.Reset}");
        Ln($"{C.Gold}╚{new string('═', inner)}╝{C.Reset}");

        var edbStr = string.IsNullOrEmpty(S.EdbPath) ? $"{C.Faint}(not set){C.Reset}" : $"{C.White}{Trunc(S.EdbPath, 62)}{C.Reset}";
        var outStr = string.IsNullOrEmpty(S.OutputDir) ? $"{C.Faint}(not set){C.Reset}" : $"{C.White}{Trunc(S.OutputDir, 62)}{C.Reset}";
        Ln($" {C.Dim}edb :{C.Reset} {edbStr}");
        Ln($" {C.Dim}out :{C.Reset} {outStr}");
        Ln($"{C.Dim}{new string('─', W)}{C.Reset}");
    }

    static void Ln(string s = "") => Console.WriteLine(s);
    static string Trunc(string s, int n) => s.Length <= n ? s : "…" + s[^(n - 1)..];
}

// ═══════════════════════════════════════════════════════════════════════════════
// STDERR COLORIZER  — intercepts service progress output, applies ANSI color
// ═══════════════════════════════════════════════════════════════════════════════
class ColorStderrWriter(TextWriter inner) : TextWriter
{
    public override Encoding Encoding => inner.Encoding;

    public override void WriteLine(string? value)
    {
        if (value is null) { inner.WriteLine(); return; }

        var color = value switch
        {
            var v when v.StartsWith("✓") => C.Green,
            var v when v.StartsWith("✗") => C.Red,
            var v when v.StartsWith("⚠") => C.Amber,
            var v when v.StartsWith("ℹ") => C.Cyan,
            var v when v.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                       v.Contains("fail", StringComparison.OrdinalIgnoreCase) => C.Red,
            _ => C.White,
        };

        inner.WriteLine($"  {color}{value}{C.Reset}");
    }

    public override void Write(string? value) => inner.Write(value);
}
