using ShiftIt;
using ShiftIt.Configuration;
using ShiftIt.Services;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service in production; runs as a console app in development.
builder.Services.AddWindowsService(options => options.ServiceName = "ShiftIt");

// Bind and validate configuration.
builder.Services
    .AddOptions<ArchiveOptions>()
    .Bind(builder.Configuration.GetSection(ArchiveOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ArchiveOptions>, ArchiveOptionsValidator>();

// Application services.
builder.Services.AddSingleton<FileMover>();
builder.Services.AddSingleton<IArchiveScanner, ArchiveScanner>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
