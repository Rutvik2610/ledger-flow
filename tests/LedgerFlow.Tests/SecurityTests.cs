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

    [Fact]
    public async Task OpenApi_exposes_required_transfer_idempotency_header()
    {
        await using var app = new Factory("Development");
        var client = app.CreateClient();
        using var document = System.Text.Json.JsonDocument.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"));
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/transfers").GetProperty("post").GetProperty("parameters");
        var header = Assert.Single(parameters.EnumerateArray(), p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.Equal("header", header.GetProperty("in").GetString());
        Assert.True(header.GetProperty("required").GetBoolean());
        Assert.Equal("string", header.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Invalid_requests_return_problem_details_without_database_access()
    {
        await using var app = new Factory("Development"); var client = app.CreateClient();
        var response = await client.PostAsync("/dev/token/alice", null);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new("Bearer", json.RootElement.GetProperty("access_token").GetString());
        var malformed = await client.PostAsync("/api/transfers", new StringContent("{", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("application/problem+json", malformed.Content.Headers.ContentType!.MediaType);
        var unknown = await client.PostAsync("/api/accounts", new StringContent("{\"currency\":\"USD\",\"balance\":100}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var missingKey = await client.PostAsync("/api/transfers", new StringContent("{\"from\":\"00000000-0000-0000-0000-000000000001\",\"to\":\"00000000-0000-0000-0000-000000000002\",\"amount\":1}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
    }
}
