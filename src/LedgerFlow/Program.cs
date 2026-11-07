using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Threading.RateLimiting;
using LedgerFlow.Application;
using LedgerFlow.Domain;
using LedgerFlow.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
var config = builder.Configuration;
if (config["Transport"] is not (null or "Local" or "Azure")) throw new InvalidOperationException("Transport must be Local or Azure.");
builder.Services.AddDbContextFactory<LedgerDb>(o => o.UseSqlServer(config.GetConnectionString("Sql"), sql => sql.CommandTimeout(Math.Clamp(config.GetValue("Sql:CommandTimeoutSeconds", 15), 1, 120))));
builder.Services.AddScoped<Transfers>();
builder.Services.AddSingleton<NotificationConsumer>();
builder.Services.AddScoped<MetadataCache>();
builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(new ConfigurationOptions { EndPoints = { config["Redis"] ?? "localhost:6379" }, AbortOnConnectFail = false, ConnectTimeout = 500, AsyncTimeout = 500 }));
builder.Services.AddSingleton<IEventTransport>(sp => config["Transport"] == "Azure" ? new AzureTransport(config) : new LocalTransport(sp.GetRequiredService<NotificationConsumer>()));
builder.Services.AddHostedService<OutboxWorker>();
builder.Services.AddHostedService<AzureConsumer>();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer(); builder.Services.AddSwaggerGen(o =>
{
    o.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme { Type = Microsoft.OpenApi.SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    o.AddSecurityRequirement(document => new Microsoft.OpenApi.OpenApiSecurityRequirement { [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document)] = [] });
});
var signingKey = RandomNumberGenerator.GetBytes(64);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    if (builder.Environment.IsDevelopment() && config.GetValue<bool>("Auth:Development")) o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidIssuer = "ledgerflow-development", ValidateAudience = true, ValidAudience = "ledgerflow", ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(signingKey!), ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(5) };
    else { o.Authority = config["Auth:Authority"] ?? throw new InvalidOperationException("Configure Auth:Authority or explicitly enable development authentication in Development."); o.Audience = config["Auth:Audience"] ?? throw new InvalidOperationException("Configure Auth:Audience."); o.RequireHttpsMetadata = true; }
});
builder.Services.AddAuthorization(o => o.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireAssertion(c => c.User.FindFirstValue("sub") is { Length: > 0 and <= 200 } subject && subject == subject.Trim() && !subject.Any(char.IsControl) && subject != "__funding__").Build());
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (context, ct) => await Results.Problem(statusCode: 429, title: "Rate limit exceeded").ExecuteAsync(context.HttpContext);
    o.AddPolicy("writes", http => RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirstValue("sub") ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = config.GetValue("RateLimit:PerMinute", 60), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
var developmentAuth = app.Environment.IsDevelopment() && config.GetValue<bool>("Auth:Development");
if (!args.Contains("--migrate") && !developmentAuth &&
    (!Uri.TryCreate(config["Auth:Authority"], UriKind.Absolute, out var authority) || authority.Scheme != "https" || string.IsNullOrWhiteSpace(config["Auth:Audience"])))
    throw new InvalidOperationException("Configure an HTTPS Auth:Authority and Auth:Audience, or explicitly enable development authentication in Development.");
app.UseExceptionHandler(error => error.Run(async http =>
{
    var ex = http.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var status = ex is Microsoft.Data.SqlClient.SqlException ? 503 : 500;
    await Results.Problem(statusCode: status, title: status == 503 ? "Database temporarily unavailable; retry with the same idempotency key." : "Unexpected server error", extensions: new Dictionary<string, object?> { { "traceId", Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier } }).ExecuteAsync(http);
}));
app.Use(async (ctx, next) => { ctx.Response.Headers["X-Correlation-ID"] = ctx.TraceIdentifier; using (app.Logger.BeginScope(new Dictionary<string, object> { { "CorrelationId", ctx.TraceIdentifier } })) await next(); });
app.UseStatusCodePages(async c => await Results.Problem(statusCode: c.HttpContext.Response.StatusCode).ExecuteAsync(c.HttpContext));
app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
app.UseSwagger(); app.UseSwaggerUI();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (LedgerDb db, CancellationToken ct) =>
{
    try { await db.Accounts.Take(1).CountAsync(ct); return Results.Ok(new { status = "ready" }); } catch { return Results.Problem(statusCode: 503, title: "SQL schema unavailable"); }
});
if (developmentAuth) app.MapPost("/dev/token/{subject}", (string subject) =>
{
    if (subject.Length is < 1 or > 100 || !subject.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return Results.Problem(statusCode: 400, title: "Use an alphanumeric synthetic subject.");
    var jwt = new JwtSecurityToken("ledgerflow-development", "ledgerflow", [new Claim("sub", subject)], expires: DateTime.UtcNow.AddHours(1), signingCredentials: new SigningCredentials(new SymmetricSecurityKey(signingKey!), SecurityAlgorithms.HmacSha256));
    return Results.Ok(new { access_token = new JwtSecurityTokenHandler().WriteToken(jwt) });
}).RequireRateLimiting("writes");
var api = app.MapGroup("/api").RequireAuthorization();
static string Owner(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? throw new InvalidOperationException("Missing subject");
static bool Page(int page, int size) => page >= 1 && page <= 100000 && size >= 1 && size <= 100;
api.MapPost("/accounts", async (CreateAccount request, ClaimsPrincipal user, LedgerDb db, CancellationToken ct) =>
{
    if (request.Currency is not ("USD" or "INR" or "EUR")) return Results.Problem(statusCode: 400, title: "Supported currencies: USD, INR, EUR (two decimal places).");
    var account = new Account { Owner = Owner(user), Currency = request.Currency }; db.Accounts.Add(account); await db.SaveChangesAsync(ct); return Results.Created($"/api/accounts/{account.Id}", account);
}).RequireRateLimiting("writes");
api.MapGet("/accounts", async (ClaimsPrincipal user, LedgerDb db, CancellationToken ct, int page = 1, int size = 20) =>
{
    if (!Page(page, size)) return Results.Problem(statusCode: 400, title: "Page >=1; size 1..100.");
    var owner = Owner(user); return Results.Ok(await db.Accounts.AsNoTracking().Where(x => x.Owner == owner).OrderBy(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct));
});
api.MapGet("/accounts/{id:guid}", async (Guid id, ClaimsPrincipal user, MetadataCache cache, CancellationToken ct) =>
{
    var result = await cache.Get(id, Owner(user), ct); return result == null ? Results.NotFound() : Results.Ok(result);
});
api.MapGet("/accounts/{id:guid}/balance", async (Guid id, ClaimsPrincipal user, LedgerDb db, CancellationToken ct) =>
{
    var owner = Owner(user); var result = await db.Accounts.AsNoTracking().Where(x => x.Id == id && x.Owner == owner).Select(x => new { x.Id, x.Balance, x.Currency }).SingleOrDefaultAsync(ct); return result == null ? Results.NotFound() : Results.Ok(result);
});
api.MapPost("/transfers", async (TransferRequest request, HttpContext http, Transfers service, CancellationToken ct) =>
{
    var key = http.Request.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 1 or > 128 || key.Any(c => c < 33 || c > 126)) return Results.Problem(statusCode: 400, title: "Idempotency-Key must contain 1..128 printable non-space ASCII characters.");
    var result = await service.Post(Owner(http.User), key, request, ct);
    return Results.Content(result.Json, result.Status >= 400 ? "application/problem+json" : "application/json", statusCode: result.Status);
}).RequireRateLimiting("writes");
api.MapGet("/accounts/{id:guid}/history", async (Guid id, ClaimsPrincipal user, LedgerDb db, CancellationToken ct, int page = 1, int size = 20, DateTimeOffset? from = null, DateTimeOffset? to = null, string? kind = null) =>
{
    if (!Page(page, size) || (from != null && to != null && from > to)) return Results.Problem(statusCode: 400, title: "Invalid pagination or date range.");
    var owner = Owner(user); if (!await db.Accounts.AnyAsync(x => x.Id == id && x.Owner == owner, ct)) return Results.NotFound();
    var query = db.Operations.AsNoTracking().Where(x => x.From == id || x.To == id);
    if (from != null) query = query.Where(x => x.CreatedAt >= from); if (to != null) query = query.Where(x => x.CreatedAt < to); if (kind != null) query = query.Where(x => x.Kind == kind);
    return Results.Ok(await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct));
});
api.MapGet("/notifications", async (ClaimsPrincipal user, LedgerDb db, CancellationToken ct, int page = 1, int size = 20) =>
{
    if (!Page(page, size)) return Results.Problem(statusCode: 400, title: "Invalid pagination");
    var owner = Owner(user);
    return Results.Ok(await (from n in db.Notifications.AsNoTracking()
                             join o in db.Operations on n.TransferId equals o.Id
                             where db.Accounts.Any(a => a.Owner == owner && (a.Id == o.From || a.Id == o.To))
                             orderby n.ReceivedAt descending, n.Id descending
                             select n).Skip((page - 1) * size).Take(size).ToListAsync(ct));
});
if (app.Environment.IsDevelopment()) api.MapPost("/dev/accounts/{id:guid}/fund", async (Guid id, Funding request, ClaimsPrincipal user, LedgerDb db, CancellationToken ct) =>
{
    if (request.Amount is <= 0 or > 1000000000) return Results.Problem(statusCode: 400, title: "Funding amount must be 1..1000000000.");
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    // Funding serializes with every funding request; target lock also participates in transfer ordering.
    await Transfers.Lock(db, "development-funding", ct); await Transfers.Lock(db, "account:" + id, ct);
    var account = await db.Accounts.FindAsync([id], ct); if (account == null || account.Owner != Owner(user)) return Results.NotFound();
    var system = await db.Accounts.SingleOrDefaultAsync(x => x.Owner == "__funding__" && x.Currency == account.Currency, ct);
    if (system == null) { system = new Account { Owner = "__funding__", Currency = account.Currency }; db.Accounts.Add(system); }
    var operation = new Operation { Kind = "DevelopmentFunding", From = system.Id, To = id, Amount = request.Amount, Currency = account.Currency };
    system.Balance = checked(system.Balance - request.Amount); account.Balance = checked(account.Balance + request.Amount);
    db.Operations.Add(operation); db.Entries.AddRange(new Entry { OperationId = operation.Id, AccountId = system.Id, Amount = -request.Amount }, new Entry { OperationId = operation.Id, AccountId = id, Amount = request.Amount });
    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Ok(operation);
}).RequireRateLimiting("writes");
if (args.Contains("--migrate")) { await using var scope = app.Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<LedgerDb>().Database.MigrateAsync(); return; }
app.Run();
public record CreateAccount(string Currency);
public record Funding(long Amount);
public partial class Program;
