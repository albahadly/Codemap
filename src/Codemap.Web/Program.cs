using Codemap.Application.Messaging;
using Codemap.Application.Scanning;
using Codemap.Infrastructure;
using Codemap.Infrastructure.Persistence;
using Codemap.Web.Components;
using Codemap.Web.Hubs;
using Codemap.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSignalR();

// All use cases flow through the custom dispatcher; scanning the Web assembly also picks up the
// ScanProgressChanged → SignalR forwarder notification handler.
builder.Services.AddCustomDispatcher(typeof(ScanRepositoryCommand).Assembly, typeof(Program).Assembly);

builder.Services.AddCodemapInfrastructure(
    builder.Configuration.GetConnectionString("Codemap")
    ?? "Server=(localdb)\\MSSQLLocalDB;Database=Codemap;Trusted_Connection=True;TrustServerCertificate=True");

builder.Services.AddScoped<KeyboardShortcutService>();

var app = builder.Build();

// Apply migrations at startup; if SQL Server is unreachable the app still runs — scans stay in memory,
// only publish/history features fail until the database comes back.
using (var scope = app.Services.CreateScope())
{
    try
    {
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CodemapDbContext>>();
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Could not migrate the Codemap database — persistence features are unavailable.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapHub<ScanProgressHub>("/hubs/scan-progress");
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
