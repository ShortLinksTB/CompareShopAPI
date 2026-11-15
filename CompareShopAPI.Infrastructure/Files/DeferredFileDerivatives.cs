using CompareShopAPI.Application.Files;
using Microsoft.Extensions.DependencyInjection;

namespace CompareShopAPI.Infrastructure.Files;

/// <summary>Defers Files storage configuration until a derivative operation is actually requested.</summary>
public sealed class DeferredFileDerivatives(IServiceProvider services) : IFileDerivatives
{
    public Task<IReadOnlyList<DerivativeVariantOutcome>> CreateDerivatives(string? subject, Guid id, DerivativeRequest request, CancellationToken ct) =>
        services.GetRequiredService<FileService>().CreateDerivatives(subject, id, request, ct);
}
