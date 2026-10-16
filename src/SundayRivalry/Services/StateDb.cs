using Microsoft.EntityFrameworkCore;

namespace SundayRivalry.Services;

public class StateRow
{
    public int Id { get; set; }
    public string Json { get; set; } = "";
}

public class StateDb(DbContextOptions<StateDb> options) : DbContext(options)
{
    public DbSet<StateRow> State => Set<StateRow>();
}
