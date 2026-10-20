extern alias Workers;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using CompareShopAPI.Domain.Admin;
using CompareShopAPI.Domain.Catalog;
using CompareShopAPI.Domain.UserCommerce;
using CompareShopAPI.Infrastructure.Persistence;
using WorkerCompliance = Workers::CompareShopAPI.Workers.Compliance;
using RabbitConnection = RabbitMQ.Client.IConnection;
using RabbitChannel = RabbitMQ.Client.IChannel;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompareShopAPI.Tests;

[Collection(SqlServerTestCollection.Name)]
public sealed class BannedItemTests(SqlServerTestFixture fixture)
{
    [Fact]
    public void DomainNormalizesBrandAndOnlyRevokesOnce()
    {
        var now = DateTime.UtcNow;
        var ban = BannedItem.ForBrand("  Acme  ", " policy ", "operator", now);
        Assert.Equal("Acme", ban.BrandName);
        Assert.Equal("ACME", ban.NormalizedBrandName);
        Assert.True(ban.IsActive);
        Assert.Throws<ArgumentException>(() => BannedItem.ForBrand(" ", "reason", "actor", now));
        Assert.Throws<ArgumentException>(() => BannedItem.ForBrand("brand", "reason", "actor", DateTime.Now));

        Assert.True(ban.Revoke("reviewer", now.AddMinutes(1)));
        Assert.False(ban.IsActive);
        Assert.Equal("reviewer", ban.RevokedBy);
        Assert.False(ban.Revoke("reviewer", now.AddMinutes(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BannedItem.ForProduct(0, "reason", "actor", now));
    }

    [Fact]
    public async Task AdminRoutesAuditOutboxAndPublicVisibilityFollowBanState()
    {
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString)
        {
            InitialCatalog = $"CompareShopBannedItems_{Guid.NewGuid():N}"
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseSqlServer(connection).Options;
        var subject = $"banned-operator-{Guid.NewGuid():N}";
        var editorSubject = $"banned-editor-{Guid.NewGuid():N}";
        int productId;
        int offerId;
        try
        {
            await using (var db = new CompareShopDbContext(options))
            {
                await db.Database.MigrateAsync();
                db.Users.Add(new User(subject, DateTime.UtcNow));
                db.Users.Add(new User(editorSubject, DateTime.UtcNow));
                var category = new Category { Slug = "banned-test-category", Name = "Banned test" };
                var shop = new PartnerShop { Slug = "banned-test-shop", Name = "Banned test shop" };
                db.Categories.Add(category);
                db.PartnerShops.Add(shop);
                await db.SaveChangesAsync();
                var product = new Product { Slug = "banned-test-product", Name = "Banned target product", Brand = "Acme", CategoryId = category.Id };
                db.Products.Add(product);
                await db.SaveChangesAsync();
                var offer = new ProductOffer { ProductId = product.Id, PartnerShopId = shop.Id, PriceVnd = 500_000, UpdatedAtUtc = DateTime.UtcNow };
                db.ProductOffers.Add(offer);
                db.AffiliateLinks.Add(new AffiliateLink { ProductOffer = offer, DestinationUrl = "https://shop.example/item", IsEnabled = true });
                db.AdminUserRoles.Add(new AdminUserRole { UserId = await db.Users.Where(x => x.KeycloakSubject == subject).Select(x => x.Id).SingleAsync(), RoleId = 2 });
                db.AdminUserRoles.Add(new AdminUserRole { UserId = await db.Users.Where(x => x.KeycloakSubject == editorSubject).Select(x => x.Id).SingleAsync(), RoleId = 3 });
                await db.SaveChangesAsync();
                productId = product.Id;
                offerId = offer.Id;
            }

            var settings = new Dictionary<string, string?> { ["ConnectionStrings:CompareShopDb"] = connection };
            await using (var app = new ApiFactory(settings: settings, signing: true))
            {
                var client = app.CreateClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                    ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: subject));

                var createdResponse = await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Brand", brandName = " acme ", reason = "Policy violation"
                });
                Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
                using var createdJson = System.Text.Json.JsonDocument.Parse(await createdResponse.Content.ReadAsStringAsync());
                var created = createdJson.RootElement;
                var banId = created.GetProperty("id").GetInt32();
                Assert.Equal("Brand", created.GetProperty("targetType").GetString());
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Brand", brandName = "ACME", reason = "Duplicate"
                })).StatusCode);
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Unknown", brandName = "ACME", reason = "Invalid"
                })).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Product", productId = int.MaxValue, reason = "Unknown product"
                })).StatusCode);

                using (var list = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/admin/compliance/bans")).Content.ReadAsStringAsync()))
                    Assert.Single(list.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/admin/compliance/bans/{banId}")).StatusCode);

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                    ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: editorSubject));
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/compliance/bans")).StatusCode);
                Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Brand", brandName = "Other", reason = "No permission"
                })).StatusCode);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
                    ApiFactory.Token("{\"compareshop-admin-ui\":{\"roles\":[\"admin\"]}}", subject: subject));

                using (var products = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/api/v1/products?search=Banned%20target")).Content.ReadAsStringAsync()))
                    Assert.Empty(products.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/products/banned-test-product")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/products/{productId}/offers")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/products/{productId}/price-history")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/go/{offerId}")).StatusCode);

                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/compliance/bans/{banId}")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/admin/compliance/bans/2147483647")).StatusCode);
                using (var active = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/admin/compliance/bans")).Content.ReadAsStringAsync()))
                    Assert.Empty(active.RootElement.GetProperty("items").EnumerateArray());
                using (var revoked = System.Text.Json.JsonDocument.Parse(await (await client.GetAsync("/admin/compliance/bans?active=false")).Content.ReadAsStringAsync()))
                    Assert.Single(revoked.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products/banned-test-product")).StatusCode);

                var productBanResponse = await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Product", productId, reason = "Product policy violation"
                });
                Assert.Equal(HttpStatusCode.Created, productBanResponse.StatusCode);
                using var productBanJson = System.Text.Json.JsonDocument.Parse(await productBanResponse.Content.ReadAsStringAsync());
                var productBanId = productBanJson.RootElement.GetProperty("id").GetInt32();
                Assert.Equal("Banned target product", productBanJson.RootElement.GetProperty("productName").GetString());
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/admin/compliance/bans", new
                {
                    targetType = "Product", productId, reason = "Duplicate product ban"
                })).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/products/banned-test-product")).StatusCode);
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/products/{productId}/offers")).StatusCode);
                Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/compliance/bans/{productBanId}")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/products/banned-test-product")).StatusCode);

                await using var db = new CompareShopDbContext(options);
                Assert.Equal(2, await db.BannedItemOutboxMessages.CountAsync(x => x.EventType == "BannedItemCreated"));
                Assert.Equal(2, await db.BannedItemOutboxMessages.CountAsync(x => x.EventType == "BannedItemRevoked"));
                Assert.Equal(4, await db.AdminAuditLogs.CountAsync(x => x.ResourceType == "banned_item"));
                Assert.Equal(2, await db.AdminAuditLogs.CountAsync(x => x.ResourceType == "banned_item" && x.Action == "create" && x.ActorSubject == subject));
            }
        }
        finally
        {
            await using var cleanup = new CompareShopDbContext(options);
            await cleanup.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task OutboxDispatcherRetriesBrokerFailureAndMarksConfirmedMessages()
    {
        var options = new DbContextOptionsBuilder<CompareShopDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        var now = DateTime.UtcNow;
        await using var db = new CompareShopDbContext(options);
        var eventId = Guid.NewGuid();
        db.BannedItemOutboxMessages.Add(new BannedItemOutboxMessage
        {
            Id = eventId, EventType = "BannedItemCreated", Payload = "{}", CreatedAtUtc = now,
            NextAttemptAtUtc = now, AttemptCount = 0
        });
        await db.SaveChangesAsync();
        var publisher = new FakePublisher { Fail = true };
        var dispatcher = new WorkerCompliance.BannedItemOutboxDispatcher(db, publisher, new FixedTimeProvider(now), NullLogger<WorkerCompliance.BannedItemOutboxDispatcher>.Instance);

        Assert.Equal(1, await dispatcher.DispatchBatch(default));
        var failed = await db.BannedItemOutboxMessages.SingleAsync();
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(nameof(InvalidOperationException), failed.LastError);
        Assert.True(failed.NextAttemptAtUtc > now);
        Assert.Null(failed.PublishedAtUtc);

        publisher.Fail = false;
        var laterDispatcher = new WorkerCompliance.BannedItemOutboxDispatcher(db, publisher, new FixedTimeProvider(failed.NextAttemptAtUtc), NullLogger<WorkerCompliance.BannedItemOutboxDispatcher>.Instance);
        Assert.Equal(1, await laterDispatcher.DispatchBatch(default));
        Assert.NotNull(failed.PublishedAtUtc);
        Assert.Equal(2, publisher.CallCount);
    }

    [Fact]
    public async Task RabbitPublisherSendsPersistentEventToPrefixedDurableQueue()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:Host"] = "broker.example",
            ["RabbitMQ:QueuePrefix"] = "tenant."
        }).Build();
        var channel = DispatchProxy.Create<RabbitChannel, RabbitChannelProxy>();
        var channelProxy = (RabbitChannelProxy)(object)channel;
        var connection = DispatchProxy.Create<RabbitConnection, RabbitConnectionProxy>();
        var connectionProxy = (RabbitConnectionProxy)(object)connection;
        connectionProxy.Channel = channel;
        await using var publisher = new WorkerCompliance.RabbitMqBannedItemMessagePublisher(configuration,
            NullLogger<WorkerCompliance.RabbitMqBannedItemMessagePublisher>.Instance, (_, _) => Task.FromResult(connection));
        var eventId = Guid.NewGuid();
        var message = new BannedItemOutboxMessage
        {
            Id = eventId, EventType = "BannedItemCreated", Payload = "{\"event\":true}",
            CreatedAtUtc = DateTime.UtcNow, NextAttemptAtUtc = DateTime.UtcNow
        };

        await publisher.Publish(message, default);

        Assert.Equal("tenant.banned-items.events", channelProxy.QueueName);
        Assert.True(channelProxy.Durable);
        Assert.Equal("tenant.banned-items.events", channelProxy.RoutingKey);
        Assert.True(channelProxy.Persistent);
        Assert.Equal(eventId.ToString("D"), channelProxy.MessageId);
        Assert.Equal("{\"event\":true}", Encoding.UTF8.GetString(channelProxy.Body));
        await publisher.DisposeAsync();
        Assert.True(connectionProxy.Disposed);
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task RabbitPublisherRejectsMissingHostAndRetriesConnectionFailureWithoutBlockingCaller()
    {
        var noHost = new WorkerCompliance.RabbitMqBannedItemMessagePublisher(new ConfigurationBuilder().Build(),
            NullLogger<WorkerCompliance.RabbitMqBannedItemMessagePublisher>.Instance);
        var message = new BannedItemOutboxMessage { Id = Guid.NewGuid(), EventType = "BannedItemCreated", Payload = "{}" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => noHost.Publish(message, default));
        await noHost.DisposeAsync();

        var unreachable = new WorkerCompliance.RabbitMqBannedItemMessagePublisher(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["RabbitMQ:Host"] = "127.0.0.1:1" }).Build(),
            NullLogger<WorkerCompliance.RabbitMqBannedItemMessagePublisher>.Instance);
        await Assert.ThrowsAnyAsync<Exception>(() => unreachable.Publish(message, default));
        await unreachable.DisposeAsync();
    }

    [Fact]
    public async Task OutboxWorkerRunsPollingLoopUntilCancelled()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContext<CompareShopDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        services.AddSingleton<WorkerCompliance.IBannedItemMessagePublisher, FakePublisher>();
        services.AddScoped<WorkerCompliance.BannedItemOutboxDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var worker = new WorkerCompliance.BannedItemOutboxWorker(provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            NullLogger<WorkerCompliance.BannedItemOutboxWorker>.Instance);
        await worker.StartAsync(default);
        await Task.Delay(50);
        await worker.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
    }

    private sealed class FakePublisher : WorkerCompliance.IBannedItemMessagePublisher
    {
        public bool Fail { get; set; }
        public int CallCount { get; private set; }
        public Task Publish(BannedItemOutboxMessage message, CancellationToken ct)
        {
            CallCount++;
            return Fail ? Task.FromException(new InvalidOperationException("broker unavailable")) : Task.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTime value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(value);
    }

    public class RabbitConnectionProxy : DispatchProxy
    {
        public RabbitChannel Channel { get; set; } = null!;
        public bool Disposed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_IsOpen": return true;
                case "CreateChannelAsync": return Task.FromResult<RabbitChannel>(Channel);
                case "DisposeAsync": Disposed = true; return ValueTask.CompletedTask;
                default: return DefaultReturn(targetMethod?.ReturnType);
            }
        }
    }

    public class RabbitChannelProxy : DispatchProxy
    {
        public string? QueueName { get; private set; }
        public string? RoutingKey { get; private set; }
        public bool Durable { get; private set; }
        public bool Persistent { get; private set; }
        public string? MessageId { get; private set; }
        public byte[] Body { get; private set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "QueueDeclareAsync")
            {
                var parameters = targetMethod.GetParameters();
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (parameters[index].Name == "queue") QueueName = args?[index] as string;
                    if (parameters[index].Name == "durable") Durable = args?[index] as bool? ?? false;
                }
                return DefaultReturn(targetMethod.ReturnType);
            }
            if (targetMethod?.Name == "BasicPublishAsync")
            {
                var parameters = targetMethod.GetParameters();
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (parameters[index].Name == "routingKey") RoutingKey = args?[index] as string;
                    if (parameters[index].Name == "basicProperties" && args?[index] is { } properties)
                    {
                        MessageId = properties.GetType().GetProperty("MessageId")?.GetValue(properties) as string;
                        Persistent = properties.GetType().GetProperty("Persistent")?.GetValue(properties) as bool? ?? false;
                    }
                    if (args?[index] is ReadOnlyMemory<byte> memory) Body = memory.ToArray();
                    if (args?[index] is byte[] bytes) Body = bytes;
                }
                return DefaultReturn(targetMethod.ReturnType);
            }
            if (targetMethod?.Name == "DisposeAsync") return ValueTask.CompletedTask;
            return DefaultReturn(targetMethod?.ReturnType);
        }
    }

    private static object? DefaultReturn(Type? returnType)
    {
        if (returnType is null || returnType == typeof(void)) return null;
        if (returnType == typeof(ValueTask)) return ValueTask.CompletedTask;
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var valueType = returnType.GetGenericArguments()[0];
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(valueType).Invoke(null, [value]);
        }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var valueType = returnType.GetGenericArguments()[0];
            var value = valueType.IsValueType ? Activator.CreateInstance(valueType) : null;
            return Activator.CreateInstance(returnType, value);
        }
        if (returnType == typeof(Task)) return Task.CompletedTask;
        return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
    }
}
