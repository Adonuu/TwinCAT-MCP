using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using TwinCatMcp.Automation;
using TwinCatMcp.Runtime;
using TwinCatMcp.Safety;
using TwinCatMcp.Source;

var builder = Host.CreateApplicationBuilder(args);

// stdio transport requires stdout to carry only protocol frames — route logs to stderr.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// --- Options: bound from appsettings.json / environment variables (see docs/claude-desktop-config.sample.json) ---
builder.Services.Configure<SafetyOptions>(builder.Configuration.GetSection(SafetyOptions.SectionName));
builder.Services.Configure<RuntimeOptions>(builder.Configuration.GetSection(RuntimeOptions.SectionName));
builder.Services.Configure<AutomationOptions>(builder.Configuration.GetSection(AutomationOptions.SectionName));

// --- Cross-cutting safety (shared by Source/Runtime/Automation tools) ---
builder.Services.AddSingleton(sp => new OperationAuditLog(
    ResolveAuditLogPath(builder.Configuration, sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SafetyOptions>>().Value),
    sp.GetRequiredService<ILogger<OperationAuditLog>>()));
builder.Services.AddSingleton<SafetyGate>();

// --- Scope 1: file-based PLC source (portable, no TwinCAT dependency) ---
builder.Services.AddSingleton(_ => new PlcProjectIndex(ResolveProjectRoot(builder.Configuration)));

// --- Scope 2: live runtime access via ADS ---
builder.Services.AddSingleton<AdsConnectionManager>();
builder.Services.AddSingleton<SymbolBrowser>();
builder.Services.AddSingleton<NotificationHub>();

// --- Scope 3: XAE Shell automation (COM, Windows-only — see XaeShellSession for the platform guard) ---
builder.Services.AddSingleton<StaThreadDispatcher>();
builder.Services.AddSingleton<XaeShellSession>();

// WithToolsFromAssembly() defaults to Assembly.GetCallingAssembly() — it does NOT scan referenced
// assemblies, so each tool-family assembly must be registered explicitly.
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly(typeof(Program).Assembly)
    .WithToolsFromAssembly(typeof(TwinCatMcp.Source.SourceTools).Assembly)
    .WithToolsFromAssembly(typeof(TwinCatMcp.Runtime.RuntimeTools).Assembly)
    .WithToolsFromAssembly(typeof(TwinCatMcp.Automation.AutomationTools).Assembly);

var host = builder.Build();

// PlcProjectIndex/AdsConnectionManager/NotificationHub/XaeShellSession/StaThreadDispatcher are all
// singletons implementing IDisposable/IAsyncDisposable — the host's root service provider disposes
// them automatically (in reverse registration order) when it shuts down, so no manual lifecycle
// hookup is needed here.
await host.RunAsync();

/// <summary>
/// Resolves the PLC project root to index from explicit config/env ("PlcProject:Root" or "PROJECT_PATH").
/// </summary>
static string ResolveProjectRoot(IConfiguration configuration)
{
    var configured = configuration["PlcProject:Root"] ?? configuration["PROJECT_PATH"];
    if (!string.IsNullOrWhiteSpace(configured))
        return configured;

    throw new InvalidOperationException(
        "No PLC project root configured — set 'PlcProject:Root' in appsettings.json or the PROJECT_PATH environment variable.");
}

/// <summary>
/// Resolves the audit log path: explicit config wins; otherwise place it under the configured PLC project
/// root so each project's audit trail travels with it, falling back to the working directory.
/// </summary>
static string ResolveAuditLogPath(IConfiguration configuration, SafetyOptions options)
{
    if (Path.IsPathRooted(options.AuditLogPath))
        return options.AuditLogPath;

    try
    {
        return Path.Combine(ResolveProjectRoot(configuration), options.AuditLogPath);
    }
    catch (InvalidOperationException)
    {
        return Path.GetFullPath(options.AuditLogPath);
    }
}
