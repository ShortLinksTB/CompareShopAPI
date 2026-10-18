using CompareShopAPI.Application.Admin;
using CompareShopAPI.Application.Articles;

namespace CompareShopAPI.Infrastructure.Articles;

public sealed class AdminArticlePermissionGate(IAdminData admin) : IArticlePermissionGate
{
    public Task<bool> HasPermission(string subject, string permission, CancellationToken ct) => admin.HasPermission(subject, permission, ct);
}
