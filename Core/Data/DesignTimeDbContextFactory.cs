using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LMP.Core.Data;

/// <summary>
/// Factory for EF Core design-time tools (migrations, scaffolding).
/// Not used at runtime.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LibraryDbContext>
{
    public LibraryDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<LibraryDbContext>();
        
        // Use a temporary path for design-time operations
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            G.AppId, G.File.Database);
        
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
        
        return new LibraryDbContext(optionsBuilder.Options);
    }
}