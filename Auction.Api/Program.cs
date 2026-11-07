using Auction.Api.Common;
using Auction.Api.Extensions;
using Auction.Api.Hubs;
using Auction.Api.Workers;
using Auction.Application;
using Auction.Application.Interfaces;
using Auction.Infrastructure;
using Auction.Infrastructure.Clients;
using Auction.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 1. Auxiliary Service Defaults (Observability, Swagger, JSON options)
builder.Services.AddServiceDefaults(builder.Configuration, "auction-service");

// Configure Kestrel endpoint defaults for HTTP/1 & HTTP/2
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

// 1b. Realtime (SignalR): đẩy sự kiện của phiên (đặt giá mới, huỷ phiên) tới người đang xem
// Client chỉ gửi JoinAuction/LeaveAuction (vài chục byte), nên giới hạn kích thước tin nhận để chặn lạm dụng.
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4 * 1024);
builder.Services.AddSingleton<IAuctionNotifier, SignalRAuctionNotifier>();

// 1c. CORS cho trình duyệt kết nối thẳng tới hub. Khi đi qua Gateway và Gateway lo CORS thì để trống Cors:AllowedOrigins.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    if (corsOrigins.Length > 0)
        policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    else if (builder.Environment.IsDevelopment())
        // SignalR client gửi credentials khi negotiate, nên không dùng AllowAnyOrigin() (trình duyệt từ chối '*' kèm credentials).
        policy.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
}));

// 2. Application & Infrastructure Dependency Injection
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// 2a. Tiến trình nền: tự chuyển Scheduled sang Running và Running sang Ended/Failed theo giờ (tắt bằng AuctionLifecycle:Enabled=false)
builder.Services.AddHostedService<AuctionLifecycleWorker>();

// 2b. Calls to other services (REST, synchronous)
var bypassUserCheck = builder.Configuration.GetValue<bool>("BypassUserCheck");
if (builder.Environment.IsProduction() && bypassUserCheck)
{
    throw new InvalidOperationException(
        "FATAL: BypassUserCheck cannot be enabled in the Production environment. Service startup aborted.");
}

if (bypassUserCheck)
    Console.WriteLine("WARNING: BypassUserCheck is ON (user service calls are skipped).");

var internalServiceKey = builder.Configuration["InternalSecurity:ServiceKey"];

builder.Services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Catalog:BaseUrl"] ?? "http://localhost:8002");
    client.Timeout = TimeSpan.FromSeconds(5);
    if (!string.IsNullOrWhiteSpace(internalServiceKey))
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", internalServiceKey);
});

builder.Services.AddSingleton(new UserClientOptions(bypassUserCheck));
builder.Services.AddHttpClient<IUserClient, UserClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["User:BaseUrl"] ?? "http://localhost:8001");
    client.Timeout = TimeSpan.FromSeconds(5); // every call sets its own, shorter timeout
    client.DefaultRequestHeaders.Add("X-Caller-Service", "auction-service");
    if (!string.IsNullOrWhiteSpace(internalServiceKey))
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", internalServiceKey);
});

// 3. REST Services
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        options.InvalidModelStateResponseFactory = ApiValidationProblemFactory.Create;
    });

var app = builder.Build();

// 4. Default Middleware Pipeline (Exception Handler, Swagger UI)
app.UseServiceDefaults();
app.UseCors();

// Automatically apply database migrations on startup if relational DB is available
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        if (db.Database.IsRelational())
        {
            var conn = db.Database.GetDbConnection();
            app.Logger.LogInformation("Applying migrations to {Host}/{Database}", conn.DataSource, conn.Database);
            db.Database.Migrate();
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Database migration failed on startup");
    }
}

app.MapControllers();
app.MapHub<AuctionHub>(AuctionHub.Route);
app.MapGet("/health", () => Results.Ok(new { status = "Healthy", service = "auction-service" }));

app.Run();