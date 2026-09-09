# Log Aggregator

**A dark-themed WPF desktop tool that ingests messy, mismatched log files — CSV, tab-delimited, flat text — and turns them into one filterable, sortable, exportable timeline.** Built for the kind of environment where every device speaks a different dialect: Kepware gateways, PI historians, Windows Event Viewer exports, and whatever else has been dumping `.txt` and `.csv` files into a folder for years.

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-0078D7)
![SQLite](https://img.shields.io/badge/storage-SQLite-003B57?logo=sqlite&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-blue)

---

## Demo

<!--
  Drop your demo GIF here and it'll show up right on the repo homepage.
  Recommended: save it as docs/demo.gif (create the docs/ folder if it
  doesn't exist yet), then this line renders it automatically — no
  other changes needed.
-->
![Log Aggregator demo](docs/demo.gif)

*GIF coming soon — drag-and-drop ingestion, adaptive timestamp detection, and live filtering across millions of rows, in about 15 seconds.*

Don't have real log files handy for a demo? **Settings → Developer → Generate Mock Log Files** spins up realistic, clearly-labeled synthetic data (`MOCK-`-prefixed server names, RFC 5737 documentation-only IPs) in any of the app's supported formats — clean for a happy-path walkthrough, or deliberately broken for showing off the parse-warning system.

---

## Description

Log Aggregator solves a specific, annoying problem: you've got log data scattered across multiple sources, none of which agree on a timestamp format, delimiter, or structure — and you need to see it all merged into one sorted, filterable view without your machine running out of memory halfway through.

- **Ingests three shapes in parallel**: CSV (full RFC4180 quoting, so embedded commas and newlines in a field don't corrupt records), tab-delimited, and flat text.
- **Detects file type and timestamp pattern automatically** — no manual "pick a format" step. Detection votes across ~30 real sampled lines rather than trusting a single line, and recognizes ~18+ distinct timestamp shapes (ISO-8601, syslog, 12-hour AM/PM, signed UTC offsets, Unix epoch, variable-precision fractional seconds, and more).
- **Never silently drops a row.** If a timestamp looks right but fails to parse, the row is kept with a sentinel value and flagged in a warnings list — you always know what happened to every line.
- **Scales to tens of millions of rows** on a SQLite backend instead of holding everything in memory. Ingestion streams straight to disk in batches; the grid is a bounded sliding window fed by paged SQL queries.
- **Filters with real boolean logic** — OR / AND / EXCLUDE term matching, translated to parameterized SQL, evaluated server-side instead of in a giant C# loop.

## Features

- Drag-and-drop ingestion (files or a `.zip`, auto-extracted) with a live-confidence quick-add path
- Reusable **LogType** definitions — one "how do I parse this" profile shared across every source that uses it
- Per-source and per-log-type color coding, with a collapsible source rail
- Click-to-sort, click-to-filter DataGrid with full-text copy support
- Streaming export of the current filtered view
- A built-in **mock log generator** (Settings → Developer) for demos and break/fix testing — invents server names, timestamps, and log content on the fly, dialable from conservative/clean to wild/deliberately-broken
- Fully custom dark theme, no default WPF chrome anywhere

## Usage

**Requirements:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Windows 10/11.

```bash
git clone https://github.com/AaronPierdon/LogAggregator.git
cd LogAggregator
dotnet build
dotnet run --project src/LogAggregator/LogAggregator.csproj
```

Or open `LogAggregator.sln` in Visual Studio 2022 (17.8+) and press F5.

**To publish a single self-contained `.exe`:**

```bash
dotnet publish src/LogAggregator/LogAggregator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output lands at `src/LogAggregator/bin/Release/net8.0-windows/win-x64/publish/LogAggregator.exe` — runs on a clean Windows machine, nothing else to install. See [`PUBLISH.md`](PUBLISH.md) for trimming and size options.

**Getting started with the app itself:**

1. Drag a log file (or several, or a `.zip`) onto the drop zone. Format and timestamp pattern are detected automatically.
2. If detection is confident, a new source appears and starts syncing immediately. If it's ambiguous, you're shown every candidate side-by-side instead of a silent guess.
3. Use the OR / AND / EXCLUDE filter bar to narrow the merged view, sort by clicking any column header, export when you're happy.
4. No real data on hand? **Settings → Developer** generates mock log files in seconds — pick an output format, how wild the timestamp patterns should get, and whether you want a clean example or a deliberately broken one to poke at the warning system.

## How it works

The interesting engineering is in `Services/TimestampDetector.cs` and `Services/LogDatabase.cs`:

- **Timestamp detection** treats "where is the timestamp" and "what format is it" as two separate, automatic questions. For flat text, a family of leading-position regexes (numeric-first, month-name-first, bracketed, epoch) locate the candidate text; for delimited files, every column is scored against ~20 candidate `.NET` format strings across a sample of real rows, and a column only qualifies if it parses successfully at least 70% of the time.
- **The SQL backend** exists because holding tens of millions of parsed rows in an `ObservableCollection` is a multi-gigabyte memory problem, not a hypothetical one. Ingestion batches writes to SQLite as it parses; the UI queries a paged, filtered, sorted window instead of holding the full result set.

## Future Vision

Things flagged as deliberate v1 trade-offs, worth revisiting:

- **True bidirectional virtualization** — right now the grid's sliding window only loads more rows going forward as you scroll; jumping back up past what's unloaded means "Jump to Start" rather than free scrollback. A proper bidirectional virtualizing data source is the natural next step.
- **Close the CSV/Tab epoch gap** — Unix-epoch and no-year timestamp shapes are only auto-detectable in flat text today; the column-based detector needs the same "best effort" fallback the flat-text path already has.
- **Config schema migration** instead of "unrecognized version starts fresh" — fine for early development, not fine forever.
- **Live tail mode** — watch an actively-growing log file update in real time instead of only syncing on demand.
- **Saved filter presets** — bookmark an OR/AND/EXCLUDE combination instead of retyping it every session.
- **A small library of built-in LogType templates** for common industrial/IT systems, so a new source can start from a known-good profile instead of always running detection from scratch.

## Project Layout

```
LogAggregator.sln
src/LogAggregator/
  App.xaml(.cs)          — entry point, global exception handling
  Themes/                — dark theme: colors, brushes, custom DataGrid + scrollbar
  Models/                — LogSource, LogType, LogBlock, TimestampProfile, AppConfig
  Services/
    LogDatabase.cs         SQLite storage, filtered/sorted/paged queries
    TimestampDetector.cs   the adaptive timestamp detection engine
    FileTypeDetector.cs    auto-detects CSV / tab-delimited / flat text
    IngestionService.cs    batched streaming ingestion, parallel per-file parsing
    MockLogGeneratorService.cs   synthetic log file generator (Developer tab)
    ExportService.cs       streams a filtered/sorted query to a flat export file
  ViewModels/ / Views/   — MVVM, hand-rolled (no third-party MVVM package)
  Converters/            — value converters
samples/                 — real-world sample logs used to design the detection engine
tests/                   — opt-in simulation harness that stress-tests detection against
                           generated data (off by default, adds zero weight to a normal build)
```
