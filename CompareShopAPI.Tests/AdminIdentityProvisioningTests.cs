using CompareShopAPI.Application.UserCommerce;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class AdminIdentityProvisioningTests
{
    [Fact]
    public async Task Blank_subject_is_rejected_without_calling_the_provisioning_port()
    {
        var users = new FakeUserProvisioning();
        var service = new AdminIdentityProvisioningService(users);

        await Assert.ThrowsAsync<ArgumentException>(() => service.Provision("  ", default));
        Assert.Equal(0, users.AdminCalls);
    }

    [Fact]
    public async Task Existing_subject_is_reused_and_reported_as_not_created()
    {
        var expected = new UserProvisioningResult(42, false);
        var users = new FakeUserProvisioning(expected);

        var result = await new AdminIdentityProvisioningService(users).Provision("existing", default);

        Assert.Equal(expected, result);
        Assert.Equal("existing", users.LastSubject);
        Assert.Equal(1, users.AdminCalls);
    }

    [Fact]
    public async Task New_subject_is_reported_as_created()
    {
        var expected = new UserProvisioningResult(73, true);
        var users = new FakeUserProvisioning(expected);

        var result = await new AdminIdentityProvisioningService(users).Provision("new", default);

        Assert.Equal(expected, result);
        Assert.True(result.Created);
    }

    private sealed class FakeUserProvisioning(UserProvisioningResult? result = null) : IUserProvisioning
    {
        public int AdminCalls { get; private set; }
        public string? LastSubject { get; private set; }

        public Task<int> GetOrCreate(string keycloakSubject, CancellationToken ct) => Task.FromResult(1);

        public Task<UserProvisioningResult> GetOrCreateForAdmin(string keycloakSubject, CancellationToken ct)
        {
            AdminCalls++;
            LastSubject = keycloakSubject;
            return Task.FromResult(result ?? new UserProvisioningResult(1, true));
        }
    }
}
