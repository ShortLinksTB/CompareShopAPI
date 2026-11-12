using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using CompareShopAPI.Api.UserCommerce;

namespace CompareShopAPI.Api.Auth;

public sealed class AuthApiJwksOptionsPostConfigure(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) : IPostConfigureOptions<JwtBearerOptions>
{
    public const string HttpClientName = "AuthAPI-Jwks";

    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        if (!string.Equals(name, AuthApiAuthenticationDefaults.Scheme, StringComparison.Ordinal))
            return;

        var documentRetriever = new HttpDocumentRetriever(httpClientFactory.CreateClient(HttpClientName))
        {
            RequireHttps = true
        };
        AuthApiJwtBearerOptions.Configure(options, configuration, documentRetriever);
    }
}
