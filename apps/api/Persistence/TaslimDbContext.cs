using Microsoft.EntityFrameworkCore;

namespace Taslim.Api.Persistence;

public sealed class TaslimDbContext(DbContextOptions<TaslimDbContext> options) : DbContext(options)
{
    // Batch 1 intentionally keeps the schema empty. Future batches can add entities here deliberately.
}
