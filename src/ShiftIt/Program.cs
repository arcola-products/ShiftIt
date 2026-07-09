using ShiftIt;
using ShiftIt.Configuration;
using ShiftIt.Services;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service in production; runs as a console app in development.
// On Windows this also wires up the Event Log as a logging provider.
builder.Services.AddWindowsService(options => options.ServiceName = "ShiftIt");

// Detailed rolling file log (Serilog), kept for a configurable number of days.
ConfigureFileLogging(builder);

// Give ShiftIt its own small, size-capped Event Log channel instead of the
// shared "Application" log, where the default ".NET Runtime" source would
// otherwise dump entries indistinguishable from any other .NET service.
if (OperatingSystem.IsWindows())
{
    ConfigureEventLog(builder);
}

// The Event Log provider only exists when hosted as a Windows Service. When run
// as a console app, suppress it so dev runs don't touch the machine's Event Log.
if (OperatingSystem.IsWindows() && !WindowsServiceHelpers.IsWindowsService())
{
    builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.None);
}

// Bind and validate configuration.
builder.Services
    .AddOptions<ArchiveOptions>()
    .Bind(builder.Configuration.GetSection(ArchiveOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ArchiveOptions>, ArchiveOptionsValidator>();

// Application services.
builder.Services.AddSingleton<IFailureTracker, FailureTracker>();
builder.Services.AddSingleton<IFileMover, FileMover>();
builder.Services.AddSingleton<IArchiveScanner, ArchiveScanner>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();

static void ConfigureFileLogging(HostApplicationBuilder builder)
{
    var section = builder.Configuration.GetSection(ArchiveOptions.SectionName);

    var logDir = section["LogDirectory"];
    if (string.IsNullOrWhiteSpace(logDir))
    {
        logDir = "logs";
    }
    if (!Path.IsPathRooted(logDir))
    {
        logDir = Path.Combine(AppContext.BaseDirectory, logDir);
    }

    var retentionDays = section.GetValue<int?>("LogRetentionDays") ?? 14;
    if (retentionDays < 1)
    {
        retentionDays = 1;
    }

    var sizeLimitMb = Math.Max(1, section.GetValue<int?>("LogFileSizeLimitMB") ?? 50);
    var fileCountLimit = Math.Max(1, section.GetValue<int?>("LogFileCountLimit") ?? 30);

    try
    {
        Directory.CreateDirectory(logDir);

        var fileLogger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            // Keep the detailed file log focused on ShiftIt; trim framework chatter.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(logDir, "shiftit-.log"),
                rollingInterval: RollingInterval.Day,
                // Hard-cap disk use: roll to a new file at the size limit and keep
                // only a bounded number of files, so an error storm can't fill the
                // disk (Serilog otherwise allows up to 1 GiB per file by default).
                fileSizeLimitBytes: (long)sizeLimitMb * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: fileCountLimit,
                retainedFileTimeLimit: TimeSpan.FromDays(retentionDays),
                outputTemplate:
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Added as an M.E.L provider, so the same category/level filters apply.
        builder.Logging.AddSerilog(fileLogger, dispose: true);
    }
    catch (Exception ex)
    {
        // Never let a logging-setup problem stop the service from running.
        Console.Error.WriteLine($"ShiftIt: file logging disabled ({ex.Message}).");
    }
}

[SupportedOSPlatform("windows")]
static void ConfigureEventLog(HostApplicationBuilder builder)
{
    // Only matters when the Event Log provider is actually active, i.e. when
    // hosted as the Windows Service (see the AddFilter<EventLogLoggerProvider>
    // call below, which suppresses it otherwise).
    if (!OperatingSystem.IsWindows() || !WindowsServiceHelpers.IsWindowsService())
    {
        return;
    }

    const string channel = "ShiftIt";

    // CA1416 doesn't see the enclosing method's [SupportedOSPlatform] guard
    // through this lambda; the IsWindows() check above already covers it.
#pragma warning disable CA1416
    builder.Services.Configure<EventLogSettings>(settings =>
    {
        settings.LogName = channel;
        settings.SourceName = channel;
    });
#pragma warning restore CA1416

    var maxKilobytes = builder.Configuration
        .GetSection(ArchiveOptions.SectionName)
        .GetValue<int?>("EventLogMaxKilobytes") ?? 1024;

    CapEventLogSize(channel, maxKilobytes);
}

/// <summary>
/// Creates the "ShiftIt" Event Log channel if needed and pins its maximum size
/// small, overwriting the oldest entries once full. Run on every startup so a
/// log that grew large under a previous configuration is reined back in.
/// </summary>
[SupportedOSPlatform("windows")]
static void CapEventLogSize(string channel, int maxKilobytes)
{
    try
    {
        if (!EventLog.SourceExists(channel))
        {
            EventLog.CreateEventSource(new EventSourceCreationData(channel, channel));
        }

        using var log = new EventLog(channel);

        // MaximumKilobytes must be a multiple of 64 KB.
        var rounded = Math.Max(64, maxKilobytes - maxKilobytes % 64);
        if (log.MaximumKilobytes != rounded)
        {
            log.MaximumKilobytes = rounded;
        }
        log.ModifyOverflowPolicy(OverflowAction.OverwriteAsNeeded, 0);
    }
    catch (Exception ex)
    {
        // Never let a logging-setup problem stop the service from running.
        Console.Error.WriteLine($"ShiftIt: could not cap Event Log size ({ex.Message}).");
    }
}
