# Publishing a self-contained single .exe

## Standard build (recommended - most reliable)

```
dotnet publish src/LogAggregator/LogAggregator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Output:
```
src/LogAggregator/bin/Release/net8.0-windows/win-x64/publish/LogAggregator.exe
```

This embeds the entire .NET 8 runtime into one `.exe` (~150 MB). Copy that one file anywhere on
a clean Windows 10/11 x64 machine and run it - no .NET install required on the target machine.
The `sources.config.json` file will be created next to wherever the `.exe` is placed/run from.

## Smaller output (optional, more fragile)

If you want a smaller file, you can try enabling trimming, but **WPF apps are notoriously
unreliable with trimming** because so much is resolved via reflection (data binding, styles,
converters by type name). If you try it and things silently stop working (a control looks
inert, a converter never fires), that's almost certainly the trimmer removing something it
thought was unused. I'd recommend leaving trimming off unless file size is a hard requirement:

```
dotnet publish src/LogAggregator/LogAggregator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=true -p:TrimMode=partial
```

## Framework-dependent (smallest, requires .NET 8 Desktop Runtime on target machine)

If every target machine already has the .NET 8 Desktop Runtime installed, you can publish a much
smaller framework-dependent single file instead:

```
dotnet publish src/LogAggregator/LogAggregator.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

## Verifying the publish

After publishing, copy just the single `LogAggregator.exe` (nothing else from the `publish`
folder is required for the true self-contained build) to a different folder or a different
machine and double-click it. If it opens straight to the dark-themed main window with an empty
card list, the publish worked.

## Rebuilding after you (or I) make source changes

Once you've built successfully once, subsequent iteration is just:
```
dotnet build          # quick check during development
dotnet publish ...     # same command as above, whenever you want a fresh .exe
```
