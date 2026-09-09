# Log Aggregator

**A dark-themed WPF desktop tool that merges messy, mismatched log files — CSV, tab-delimited, flat text — into one filterable, sortable, exportable timeline.** Built for environments where every device speaks a different dialect: Kepware gateways, PI historians, Windows Event Viewer exports, whatever's been dumping `.txt`/`.csv` into a folder for years.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-0078D7)
![SQLite](https://img.shields.io/badge/storage-SQLite-003B57?logo=sqlite&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-blue)

---

## Demo

<!--
  Drop your demo GIF here and it'll show up right on the repo homepage.
  Save it as docs/demo.gif (create the docs/ folder if it doesn't exist) —
  this line picks it up automatically, no other changes needed.
-->
![Log Aggregator demo](docs/demo.gif)

*GIF coming soon — drag-and-drop ingestion, adaptive timestamp detection, and live filtering across millions of rows, in about 15 seconds.*

No real logs handy? **Settings → Developer → Generate Mock Log Files** creates realistic, clearly-labeled synthetic data (`MOCK-` server names, documentation-only IPs) — clean for a walkthrough, or deliberately broken to show off the parse-warning system.

---

## What it does

You've got log data from multiple sources, none of which agree on timestamp format, delimiter, or structure — and you need it merged into one sorted, filterable view without running out of memory.

- **Reads three shapes**: CSV (full quoting — embedded commas/newlines don't break records), tab-delimited, and flat text.
- **Detects format and timestamp pattern automatically.** No manual setup step. Recognizes 18+ timestamp shapes: ISO-8601, syslog, 12-hour, UTC offsets, Unix epoch, and more.
- **Never silently drops a row.** A timestamp that looks right but fails to parse is kept and flagged, not dropped — you always know what happened to every line.
- **Scales to tens of millions of rows** on a SQLite backend instead of holding everything in memory.
- **Filters with real logic** — OR / AND / EXCLUDE terms, run as SQL server-side rather than looped in C#.

Other features: drag-and-drop ingestion (including `.zip`), reusable **LogType** profiles, per-source color coding, click-to-sort/filter grid, streaming export, and a built-in **mock log generator** (Settings → Developer) for demos and break/fix testing.

## Usage

**Requirements:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Windows 10/11.

```bash
git clone https://github.com/AaronPierdon/LogAggregator.git
cd LogAggregator
dotnet build
dotnet run --project src/LogAggregator/LogAggregator.csproj
```

Or open `LogAggregator.sln` in Visual Studio 2022 (17.8+) and press F5.

**Single-file `.exe`:**

```bash
dotnet publish src/LogAggregator/LogAggregator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Lands at `.../bin/Release/net8.0-windows/win-x64/publish/LogAggregator.exe` — runs on a clean Windows machine, nothing else to install. See [`PUBLISH.md`](PUBLISH.md) for trimming/size options.

**Using the app:**

1. Drag a log file (or several, or a `.zip`) onto the drop zone — format and timestamp are detected automatically.
2. Confident match → the source starts syncing immediately. Ambiguous → every candidate is shown side-by-side instead of a silent guess.
3. Narrow the merged view with the OR / AND / EXCLUDE filter bar, sort by clicking a column, export when ready.
4. No real data? **Settings → Developer** generates mock log files in seconds.

## How it works

*Skip this section unless you're curious about the internals.*

**Timestamp detection** treats "where's the timestamp" and "what format is it" as separate questions. Flat text uses leading-position regexes (numeric-first, month-name-first, bracketed, epoch); delimited files score every column against ~20 candidate format strings across sampled rows, qualifying a column only if it parses successfully 70%+ of the time.

Parsing itself is layered: each candidate format is tried first through **NodaTime**, which is explicit about timezone and century assumptions instead of relying on .NET's ambient OS-locale defaults. If NodaTime's stricter parser rejects a format, it falls back automatically to `DateTime.TryParseExact` — so NodaTime only *adds* parsing coverage, it never narrows what the app could already parse before it was introduced.

**The SQL backend** exists because holding tens of millions of parsed rows in memory is a real problem, not a hypothetical one. Ingestion streams to SQLite in batches; the UI queries a paged, filtered, sorted window instead of holding the full result set.

## Future Vision

- **Bidirectional grid virtualization** — scrolling back past what's unloaded currently means "Jump to Start," not free scrollback.
- **Close the CSV/Tab epoch gap** — epoch and no-year timestamps are only auto-detected in flat text today; delimited files need the same fallback.
- **Config schema migration** instead of "unrecognized version starts fresh."
- **Live tail mode** for actively-growing files.
- **Saved filter presets.**
- **Built-in LogType template library** for common industrial/IT systems.

## Project Layout

```
LogAggregator.sln
src/LogAggregator/
  App.xaml(.cs)          — entry point, global exception handling
  Themes/                — dark theme: colors, brushes, custom controls
  Models/                — LogSource, LogType, LogBlock, TimestampProfile, AppConfig
  Services/
    LogDatabase.cs         SQLite storage, filtered/sorted/paged queries
    TimestampDetector.cs   adaptive timestamp detection (NodaTime + fallback)
    FileTypeDetector.cs    auto-detects CSV / tab-delimited / flat text
    IngestionService.cs    batched streaming ingestion, parallel per-file parsing
    MockLogGeneratorService.cs   synthetic log file generator (Developer tab)
    ExportService.cs       streams a filtered/sorted query to a flat export file
  ViewModels/ / Views/   — hand-rolled MVVM, no third-party package
  Converters/            — value converters
samples/                 — real-world sample logs used to design detection
tests/                   — opt-in simulation harness (off by default)
```
