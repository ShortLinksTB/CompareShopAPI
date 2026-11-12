using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;

namespace CompareShopAPI.Api.Auth;

public static class AuthApiJwtBearerOptions
{
    public static void Configure(JwtBearerOptions options, IConfiguration configuration, IDocumentRetriever documentRetriever)
    {
        var authority = configuration["AuthAPI:Authority"];
        var issuer = configuration["AuthAPI:Issuer"];
        var audience = configuration["AuthAPI:Audience"];
        var jwksUri = configuration["AuthAPI:JwksUri"];
        if (string.IsNullOrWhiteSpace(jwksUri) && !string.IsNullOrWhiteSpace(authority))
        {
            jwksUri = $"{authority.TrimEnd('/')}/api/v1/auth/jwks";
        }

        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = string.IsNullOrWhiteSpace(issuer) ? null : issuer,
            ValidateAudience = true,
            ValidAudience = string.IsNullOrWhiteSpace(audience) ? null : audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            NameClaimType = "sub"
        };

        if (!string.IsNullOrWhiteSpace(jwksUri) && !string.IsNullOrWhiteSpace(issuer))
        {
            options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                jwksUri,
                new AuthApiJwksConfigurationRetriever(issuer),
                documentRetriever)
            {
                AutomaticRefreshInterval = TimeSpan.FromMinutes(5),
                RefreshInterval = TimeSpan.FromMinutes(1)
            };
        }
        else if (!string.IsNullOrWhiteSpace(authority))
        {
            options.Authority = authority;
        }
    }
}

public sealed class AuthApiJwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address,
        IDocumentRetriever retriever,
        CancellationToken cancel)
    {
        var document = await retriever.GetDocumentAsync(address, cancel);
        var jwks = new JsonWebKeySet(document);
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };

        foreach (var key in jwks.GetSigningKeys())
        {
            configuration.SigningKeys.Add(key);
        }

        if (configuration.SigningKeys.Count == 0)
        {
            throw new InvalidOperationException("AuthAPI JWKS did not contain any signing keys.");
        }

        return configuration;
    }
}
