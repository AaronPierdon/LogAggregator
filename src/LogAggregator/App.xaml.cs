using System;
using System.Windows;
using System.Windows.Threading;
#if SIMULATION_MODE
// SIMULATION MODE - only exists in builds compiled with <IncludeSimulationMode>true</...>
// (see the "Simulation mode" comment block near the bottom of LogAggregator.csproj). A normal
// Debug/Release build never defines SIMULATION_MODE, so this using and everything else guarded
// by #if SIMULATION_MODE below is compiled out entirely and LogAggregator.SampleData isn't
// even referenced - the app's default build/behavior is completely unaffected by any of this.
using LogAggregator.SampleData;
#endif

namespace LogAggregator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
#if SIMULATION_MODE
        // "--simulate" on the command line skips the normal UI entirely and instead runs the
        // timestamp-detector stress test (tests/LogAggregator.SampleData + the real
        // TimestampDetector engine), writes a report, and shows a one-line summary. See
        // TryHandleSimulationArgs below for the rest of the supported flags.
        if (TryHandleSimulationArgs(e.Args))
        {
            Shutdown();
            return;
        }
#endif

        base.OnStartup(e);
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Log Aggregator - Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        MessageBox.Show(
            $"A fatal error occurred:\n\n{ex?.Message}",
            "Log Aggregator - Fatal Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

#if SIMULATION_MODE
    // ===================================================================================
    // SIMULATION MODE - everything below only compiles when IncludeSimulationMode is turned
    // on (see LogAggregator.csproj). Safe to delete this whole #if block (and the matching
    // #if block above) if this feature is ever removed instead of just left off - see the
    // README note this feature adds for the removable-vs-permanent discussion.
    // ===================================================================================

    /// <summary>Returns true - and has already shown a summary MessageBox - if
    /// <paramref name="args"/> requested simulation mode. Supported flags:
    ///   --simulate                required - turns this mode on at all
    ///   --simulate-seed=N         optional, default 42 - same seed always generates the same files
    ///   --simulate-groups=N       optional, default 6 - each group is 4 files (2 CSV + 2 TXT)
    ///   --simulate-records=N      optional, default 30 - records per generated file
    /// </summary>
    private static bool TryHandleSimulationArgs(string[] args)
    {
        if (Array.IndexOf(args, "--simulate") < 0) return false;

        var options = new SimulationOptions
        {
            Seed = ReadIntArg(args, "--simulate-seed=", 42),
            GroupCount = ReadIntArg(args, "--simulate-groups=", 6),
            RecordsPerFile = ReadIntArg(args, "--simulate-records=", 30)
        };

        var report = SimulationRunner.Run(options);

        var reportPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"LogAggregator-simulation-report-{report.RunAtUtc:yyyyMMdd_HHmmss}.txt");
        System.IO.File.WriteAllText(reportPath, report.ToReportText());

        MessageBox.Show(
            $"Simulation complete.\n\n" +
            $"Files:      {report.FilesPassed}/{report.TotalFiles} passed\n" +
            $"Data lines: {report.TotalLinesPassed}/{report.TotalDataLines} matched\n\n" +
            $"Full report written to:\n{reportPath}",
            "LogAggregator - Simulation Mode",
            MessageBoxButton.OK,
            report.FilesFailed == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        return true;
    }

    private static int ReadIntArg(string[] args, string prefix, int defaultValue)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg.AsSpan(prefix.Length), out var value))
            {
                return value;
            }
        }
        return defaultValue;
    }
#endif
}
