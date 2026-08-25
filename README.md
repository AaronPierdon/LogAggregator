# Log Aggregator

A dark-themed WPF (.NET 8) desktop app that ingests multiple log sources of different shapes
(CSV, tab-delimited, flat text) in parallel, normalizes their timestamps, and lets you filter,
sort, and export the merged result. Built from `Log_Aggregator_Initial_Prompt.docx`.

## Status / how to build this the first time

**I could not compile or run this project myself** - the sandbox I built it in has no `dotnet`
SDK and no network access for NuGet restore. Everything here was written carefully by hand and
cross-checked line by line, but there is a real chance you'll hit a small build error on the
first `dotnet build`. That's expected - see "If the first build fails" below.

1. Install the **.NET 8 SDK** if you don't have it: https://dotnet.microsoft.com/download/dotnet/8.0
2. Open a terminal in this folder and run:
   ```
   dotnet build
   ```
3. If it builds clean, run it:
   ```
   dotnet run --project src/LogAggregator/LogAggregator.csproj
   ```
   or open `LogAggregator.sln` in Visual Studio 2022 (17.8+) and press F5.

### If the first build fails

Send me the exact error text (or just hand me this folder back once you can - I built this to
be handed back and iterated on). Most likely failure modes, roughly in order of probability:

- A XAML `StaticResource` name typo somewhere (I renamed a few resources mid-build) - the error
  will name the exact key and file.
- A binding path typo caught only at runtime, not build time (WPF bindings fail silently in the
  Output window, not as build errors - if the app runs but a control looks wrong/inert, check
  the VS "Output" window for `System.Windows.Data Error` while the app is running).
- A missing `using` directive I missed during the audit pass - the compiler error names the
  missing type directly.

None of these should be more than a few minutes of work each.

## Publishing a single self-contained .exe

```
dotnet publish src/LogAggregator/LogAggregator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The output `.exe` will be at:
```
src/LogAggregator/bin/Release/net8.0-windows/win-x64/publish/LogAggregator.exe
```

This is a single file with the entire .NET runtime embedded - it will run on a clean Windows 10/11
x64 machine with nothing else installed. It will be large (~150 MB) because the runtime is
embedded; that's expected and correct for "self-contained single exe." See `PUBLISH.md` for more
detail and options (trimming, smaller output, etc.).

## Project layout

```
LogAggregator.sln
src/LogAggregator/
  App.xaml(.cs)              - app entry point, global exception handling
  Themes/                    - dark theme: colors, brushes, animations, custom scrollbar,
                               custom DataGrid template with animated row selection
  Models/                    - LogSource, LogBlock, TimestampProfile, FileType, AppConfig
  Services/
    TimestampDetector.cs     - the adaptive timestamp detection engine (see below)
    DelimitedLineParser.cs   - RFC4180 CSV reader + simple tab/line splitter
    IngestionService.cs      - adaptive buffered/streaming ingestion, block detection,
                               parallel per-file parsing, cancellation
    ConfigService.cs         - sources.config.json load/save
    FilterService.cs         - the three-box OR/AND/Exclusion filter logic
    ExportService.cs         - pipe-delimited flat-text export
  ViewModels/                - MainViewModel, SourceCardViewModel, WizardViewModel
  Views/                     - MainWindow, WizardWindow, SourceCardTemplate
  Converters/                - value converters (color tinting, visibility, etc.)
samples/                     - small excerpts of the 5 sample logs you uploaded, used to
                               design and sanity-check the timestamp detection logic
```

## How adaptive timestamp detection works (v2 - file type & pattern both automatic now)

You asked me to look at your actual sample logs and figure out how *I* would identify a
timestamp in each one, then generalize that into something automatic - and later, to make
detection run against real file content instead of a pasted sample line, and to ask you when
it's genuinely ambiguous rather than guess. Here's the current design:

**File type is never asked for.** `Services/FileTypeDetector.cs` reads the first ~25 lines of
whichever file you pick and decides CSV / Tab-delimited / Flat text on its own, by checking only
the *leading* ~80 characters of each line for delimiters. This matters because a file like
Kepware's can have plenty of commas deep inside its free-text message field
(`"Vendor ID = 1, Product type = 14, ..."`) without being comma-delimited at all - what actually
distinguishes real CSV is a delimiter showing up consistently near the start of the line, where
structured columns live. (This also turned out to be the root cause of an earlier tab-delimited
bug: Kepware.txt has zero real tab characters - it's space-padded - so no column index could
ever have worked there. It's now auto-detected as flat text instead, which handles it correctly.)

**Timestamp detection votes across ~30 real lines, not one.** `Services/TimestampDetector.cs`'s
`AutoDetectFromLines()` reads real lines straight from the file you selected (no pasting, ever)
and:
- For flat text: tests the leading-position regex against every sampled line and requires most
  of them to agree.
- For CSV/Tab: tests every column (and every adjacent date+time column pair) against every
  sampled row, and only treats a column as a candidate if it parses successfully across at
  least 70% of rows.

**Ambiguity is surfaced, not guessed.** If detection finds more than one column that
consistently looks like a timestamp, the wizard shows all of them side by side (with a live
example parse and match rate for each) and asks you to pick - it never silently picks the first
match. If nothing is found at all, manual entry appears, with the "Date column" / "Time column"
labels shown only when they're actually applicable (never a bare "column index"), and the
manual pattern is tested against the same real sample lines rather than a hand-typed example.

| Sample file | Shape | How it's detected |
|---|---|---|
| `EventViewer_Logs.txt` | CSV-ish, `MM-dd-yyyy HH:mm:ss` in column 1 | Single delimited column, full date+time match across sampled rows |
| `Event_Viewer_Logs.csv` | Real CSV with **quoted fields containing embedded commas and newlines**, `M/d/yyyy h:mm:ss tt` in column 1 | RFC4180-correct CSV reader (a naive `Split(',')` would have corrupted these records) + 12-hour AM/PM format |
| `Kepware.txt` | Flat text, **date and time separated by multiple spaces**, no real delimiter anywhere | Auto-detected as flat text (zero tabs, no leading commas); leading regex tolerant of arbitrary whitespace between date and time tokens |
| `PIMessageLog_*.csv` | CSV, `MM-dd-yyyy HH:mm:ss` in column 2, fields have leading spaces after commas | Delimited column detection with field trimming |
| `PISDK_Logs.txt` | Comma-delimited despite the `.txt` extension, **variable-precision fractional seconds** (`20:21:31.14983`, 5 digits) | Fractional-seconds normalization: whatever precision the log actually wrote gets padded/truncated to match the target format, so 3, 5, or 7 digits all parse correctly |

Whatever shape is found gets tried against a library of ~20 explicit `.NET` format strings
(covering the shapes above and a handful of others), then a culture-invariant free-form
`DateTime.TryParse` as a last resort.

At ingestion time, a timestamp that *looks* like it matches the pattern but fails to parse is
never silently dropped - the row is kept with a sentinel timestamp and a warning is logged
(visible via the warning counter in the status bar), per the original spec's "no data silently
dropped" requirement.

## Wizard flow (v2)

The wizard now asks for things in this order: **Files → Detected Pattern (review/override) →
Name → Color**. File type and timestamp are both filled in automatically before you ever see
them; the Name step is pre-suggested from the filename. You can drag & drop files (or a `.zip`,
which is extracted automatically) directly onto the file step, or use the Browse button.

The main window also has a persistent quick-add drop zone above the filter bar: drag files (or a
zip) onto it and, if detection is fully confident, a new source is created and synced with zero
prompts. If detection isn't confident enough to do that safely, it declines and points you at
"+ Add Source" instead, so a shaky guess is never silently applied. The drop zone can be hidden
via its own minimize button (Settings > Interface to bring it back - a bottom toast confirms
this when you hide it).

## Interface notes

- Both windows use a custom borderless dark title bar (no OS chrome) with custom
  minimize/maximize/close buttons, built on WPF's `WindowChrome` (part of `PresentationFramework`,
  no extra package needed).
- The Message column in the grid is a real (read-only) text box per cell, so you can select a
  substring and copy it with Ctrl+C or the native right-click Copy, in addition to a "Copy Full
  Message" menu item that copies the whole entry regardless of selection.
- All four grid columns (Source, Universal Timestamp, Original Timestamp, Message) are
  sortable by clicking the header.

## Design decisions I made without asking (flagged, not hidden)

- **.NET 8 (LTS)** rather than .NET 9, for longer-term build stability.
- **No third-party MVVM package** (no CommunityToolkit.Mvvm, etc.) - I hand-wrote a minimal
  `ObservableObject`/`RelayCommand`. This was a deliberate call given I can't compile-test here:
  fewer dependencies means fewer places a NuGet version mismatch could break your first build.
- **Color picker**: preset palette (10 colors) + a hex text box for full custom entry, rather
  than a full RGB-slider picker - matches the spec's "color picker (or palette of presets)"
  literally while staying simple.
- **Standard window chrome** (default Windows title bar), not a custom borderless chrome -
  the spec didn't request custom chrome and it meaningfully increases risk/complexity for a
  hand-written, uncompiled first pass.
- Re-syncing a source after editing it in the wizard always happens automatically if it has
  files (a changed file type or timestamp pattern needs a re-parse anyway).
- Added a "Delete Source" button in the edit wizard (not in the original spec, but there was no
  other way to remove a source once added).

## What I could not verify

I have no Windows machine, no .NET SDK, and no network access in the environment I built this
in, so **none of this has actually been run**. I traced every binding, converter, and command
path by hand and fixed several real bugs that way (a `Style` applied to the wrong control type,
a couple of missing `using` directives, a `Visibility` converter fed an `int` instead of a
`bool`), but a hand-review is not a substitute for a compiler. Please treat the first build as a
checkpoint, not a guarantee - and once you've got it building, hand the folder back to me and
I'll keep iterating directly against real build errors and your feedback using the actual files.
