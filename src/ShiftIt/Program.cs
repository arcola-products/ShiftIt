using ShiftIt;
using ShiftIt.Configuration;
using ShiftIt.Services;
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
