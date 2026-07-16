using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Codemap.Infrastructure.Persistence;

/// <summary>Lets `dotnet ef migrations add` run against this assembly without booting the Web host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CodemapDbContext>
{
    public CodemapDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CodemapDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Codemap;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new CodemapDbContext(options);
    }
}
