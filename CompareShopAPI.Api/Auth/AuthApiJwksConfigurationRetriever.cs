using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CompareShopAPI.Api.Auth;

public sealed class AuthApiJwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever documentRetriever,
        CancellationToken cancel)
    {
        var document = await documentRetriever.GetDocumentAsync(address, cancel);
        var keySet = JsonWebKeySet.Create(document);
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };

        foreach (var key in keySet.GetSigningKeys())
        {
            if (key is RsaSecurityKey or JsonWebKey { Kty: "RSA" })
                configuration.SigningKeys.Add(key);
        }

        return configuration;
    }
}
