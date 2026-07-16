using Codemap.Application.Abstractions;
using Codemap.Infrastructure.CrossLanguage;
using Codemap.Infrastructure.JavaScript;
using Codemap.Infrastructure.Persistence;
using Codemap.Infrastructure.Roslyn;
using Jering.Javascript.NodeJS;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Codemap.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddCodemapInfrastructure(this IServiceCollection services, string connectionString)
    {
        // Persistence — factory-based so background scan work never shares a long-lived context.
        services.AddDbContextFactory<CodemapDbContext>(options => options.UseSqlServer(connectionString));
        services.AddScoped<IGraphRepository, EfGraphRepository>();
        services.AddScoped<IScanHistoryRepository, EfScanHistoryRepository>();
        services.AddSingleton<IScanResultStore, InMemoryScanResultStore>();

        // Analysis engines.
        services.AddSingleton<IWorkspaceLoader, MsBuildWorkspaceLoader>();
        services.AddSingleton<CSharpWorkspaceAnalyzer>();
        services.AddSingleton<JavaScriptWorkspaceAnalyzer>();
        services.AddSingleton<IWorkspaceAnalyzer, CompositeWorkspaceAnalyzer>();
        services.AddSingleton<ICrossLanguageEdgeResolver, CrossLanguageEdgeResolver>();

        // Node bridge for the JS/TS analyzer; ProjectPath makes require() resolve the bundled node_modules.
        services.AddNodeJS();
        services.Configure<NodeJSProcessOptions>(options =>
        {
            options.ProjectPath = JavaScriptWorkspaceAnalyzer.ScriptDirectory;
        });

        return services;
    }
}
