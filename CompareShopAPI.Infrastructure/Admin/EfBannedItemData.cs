using System.Data;
using System.Text.Json;
using CompareShopAPI.Application.Admin;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CompareShopAPI.Infrastructure.Admin;

public sealed class EfBannedItemData(CompareShopDbContext db, TimeProvider clock) : IBannedItemData
{
    public async Task<Page<BannedItemDto>> List(BannedItemListFilter filter, CancellationToken ct)
    {
        var query = db.BannedItems.AsNoTracking().Include(item => item.Product).AsQueryable();
        query = query.Where(item => item.IsActive == filter.Active);
        if (filter.TargetType is not null)
        {
            var targetType = Enum.Parse<BannedTargetType>(filter.TargetType, true);
            query = query.Where(item => item.TargetType == targetType);
        }
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim();
            query = query.Where(item => item.Reason.Contains(search)
                || item.BrandName != null && item.BrandName.Contains(search)
                || item.ProductId != null && item.Product!.Name.Contains(search));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(item => item.CreatedAtUtc).ThenByDescending(item => item.Id)
            .Skip(Offset(filter.Page, filter.PageSize)).Take(filter.PageSize).ToListAsync(ct);
        return new(rows.Select(item => ToDto(item, item.Product?.Name)).ToArray(), filter.Page, filter.PageSize, total);
    }

    public async Task<BannedItemDto?> Get(int id, CancellationToken ct)
    {
        var item = await db.BannedItems.AsNoTracking().Include(x => x.Product).SingleOrDefaultAsync(x => x.Id == id, ct);
        return item is null ? null : ToDto(item, item.Product?.Name);
    }

    public Task<bool> ProductExists(int id, CancellationToken ct) => db.Products.AsNoTracking().AnyAsync(product => product.Id == id, ct);

    public async Task<BannedItemDto> Create(BannedItem item, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.BannedItems.Add(item);
            await db.SaveChangesAsync(ct);
            var productName = item.ProductId is int productId
                ? await db.Products.AsNoTracking().Where(product => product.Id == productId).Select(product => product.Name).SingleAsync(ct)
                : null;
            var dto = ToDto(item, productName);
            var occurredAtUtc = clock.GetUtcNow().UtcDateTime;
            var message = new BannedItemEvent(Guid.NewGuid(), "BannedItemCreated", item.Id, item.TargetType.ToString(),
                item.BrandName, item.ProductId, item.Reason, item.CreatedBy, occurredAtUtc);
            db.AdminAuditLogs.Add(new AdminAuditLog
            {
                ActorSubject = item.CreatedBy,
                Action = "create",
                ResourceType = "banned_item",
                ResourceId = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CreatedAtUtc = occurredAtUtc,
                AfterJson = JsonSerializer.Serialize(dto)
            });
            db.BannedItemOutboxMessages.Add(ToOutbox(message));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return dto;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new BannedItemConflictException();
        }
    }

    public async Task<bool> Revoke(int id, string actor, DateTime nowUtc, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var item = await db.BannedItems.Include(x => x.Product).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (item is null)
        {
            await transaction.RollbackAsync(ct);
            return false;
        }

        var before = ToDto(item, item.Product?.Name);
        if (!item.Revoke(actor, nowUtc))
        {
            await transaction.CommitAsync(ct);
            return true;
        }

        await db.SaveChangesAsync(ct);
        var after = ToDto(item, item.Product?.Name);
        var message = new BannedItemEvent(Guid.NewGuid(), "BannedItemRevoked", item.Id, item.TargetType.ToString(),
            item.BrandName, item.ProductId, item.Reason, actor, nowUtc);
        db.AdminAuditLogs.Add(new AdminAuditLog
        {
            ActorSubject = actor,
            Action = "revoke",
            ResourceType = "banned_item",
            ResourceId = item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            CreatedAtUtc = nowUtc,
            BeforeJson = JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after)
        });
        db.BannedItemOutboxMessages.Add(ToOutbox(message));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private static BannedItemOutboxMessage ToOutbox(BannedItemEvent message) => new()
    {
        Id = message.EventId,
        EventType = message.EventType,
        Payload = JsonSerializer.Serialize(message),
        CreatedAtUtc = message.OccurredAtUtc,
        NextAttemptAtUtc = message.OccurredAtUtc
    };

    private static BannedItemDto ToDto(BannedItem item, string? productName) => new(item.Id, item.TargetType.ToString(),
        item.BrandName, item.ProductId, productName, item.Reason, item.IsActive, item.CreatedBy, item.CreatedAtUtc,
        item.RevokedBy, item.RevokedAtUtc);

    private static int Offset(int page, int pageSize) => (int)Math.Min(int.MaxValue, ((long)page - 1) * pageSize);
    private static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException is SqlException { Number: 2601 or 2627 };
}
