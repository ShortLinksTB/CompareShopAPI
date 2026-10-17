using Microsoft.EntityFrameworkCore;
using CompareShopAPI.Domain.Catalog;
namespace CompareShopAPI.Infrastructure.Persistence;
public sealed class CompareShopDbContext(DbContextOptions<CompareShopDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<PartnerShop> PartnerShops => Set<PartnerShop>();
    public DbSet<ProductOffer> ProductOffers => Set<ProductOffer>();
    public DbSet<SpecificationGroup> SpecificationGroups => Set<SpecificationGroup>();
    public DbSet<SpecificationItem> SpecificationItems => Set<SpecificationItem>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Category>().HasKey(x => x.Id); model.Entity<Category>().HasIndex(x => x.Slug).IsUnique(); model.Entity<Category>().HasIndex(x => x.IsActive);
        model.Entity<Category>().Property(x => x.Slug).HasMaxLength(160); model.Entity<Category>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<PartnerShop>().HasKey(x => x.Id); model.Entity<PartnerShop>().HasIndex(x => x.Slug).IsUnique(); model.Entity<PartnerShop>().HasIndex(x => x.IsActive);
        model.Entity<PartnerShop>().Property(x => x.Slug).HasMaxLength(160); model.Entity<PartnerShop>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<Product>().HasKey(x => x.Id); model.Entity<Product>().HasIndex(x => x.Slug).IsUnique();
        model.Entity<Product>().HasIndex(x => new { x.IsActive, x.CategoryId }); model.Entity<Product>().Property(x => x.Slug).HasMaxLength(200);
        model.Entity<Product>().Property(x => x.Name).HasMaxLength(300); model.Entity<Product>().Property(x => x.Brand).HasMaxLength(200);
        model.Entity<Product>().HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProductOffer>().HasKey(x => x.Id); model.Entity<ProductOffer>().HasIndex(x => new { x.ProductId, x.IsActive, x.IsOutlier });
        model.Entity<ProductOffer>().HasIndex(x => new { x.PartnerShopId, x.ProductId }); model.Entity<ProductOffer>().HasIndex(x => new { x.PriceVnd, x.UpdatedAtUtc });
        model.Entity<ProductOffer>().HasOne(x => x.Product).WithMany(x => x.Offers).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ProductOffer>().HasOne(x => x.PartnerShop).WithMany().HasForeignKey(x => x.PartnerShopId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<SpecificationGroup>().HasOne(x => x.Product).WithMany(x => x.SpecificationGroups).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SpecificationItem>().HasOne(x => x.Group).WithMany(x => x.Items).HasForeignKey(x => x.SpecificationGroupId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<SpecificationGroup>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<SpecificationItem>().Property(x => x.Name).HasMaxLength(200); model.Entity<SpecificationItem>().Property(x => x.Value).HasMaxLength(1000);
    }
}
