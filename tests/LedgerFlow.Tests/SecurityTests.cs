using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;
namespace LedgerFlow.Tests;

public class SecurityTests
{
    private sealed class Factory(string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(environment).ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>{
            {"ConnectionStrings:Sql","Server=localhost,6499;Database=missing;Integrated Security=True;Connect Timeout=1;Encrypt=True"},
            {"Auth:Development","true"},{"Auth:Authority","https://identity.invalid"},{"Auth:Audience","ledgerflow"}
        }));
    }
    [Fact]
    public async Task Production_never_exposes_development_token_or_funding()
    {
        await using var app = new Factory("Production"); var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/dev/token/alice", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/dev/accounts/{Guid.NewGuid()}/fund", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready")).StatusCode);
    }
    [Fact]
    public async Task Development_token_is_short_lived_and_protected_api_requires_bearer()
    {
        await using var app = new Factory("Development"); var client = app.CreateClient();
        var response = await client.PostAsync("/dev/token/alice", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("access_token").GetString());
        Assert.Equal("alice", token.Subject);
        Assert.Equal("ledgerflow-development", token.Issuer);
        Assert.InRange(token.ValidTo, DateTime.UtcNow.AddMinutes(59), DateTime.UtcNow.AddMinutes(61));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }
}
