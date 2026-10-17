using Microsoft.EntityFrameworkCore;
namespace CompareShopAPI.Infrastructure.Persistence;
// Phase 0 deliberately has no business entities or migrations.
public sealed class CompareShopDbContext(DbContextOptions<CompareShopDbContext> options) : DbContext(options);
