using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IisControlCenter.Data;

/// <summary>Used only by 'dotnet ef'. The connection string here is never used at runtime.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=IisControlCenter;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
