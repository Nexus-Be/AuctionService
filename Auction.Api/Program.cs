using Auction.Api.Common;
using Auction.Api.Extensions;
using Auction.Api.Hubs;
using Auction.Api.Workers;
using Auction.Application;
using Auction.Application.Interfaces;
using Auction.Infrastructure;
using Auction.Infrastructure.Clients;
using Auction.Infrastructure.Notifications;
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

// Đăng ký TimeProvider để các Service sử dụng thời gian hệ thống
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);

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

// Timeout gọi User service cấu hình được (mili giây): User:TimeoutMs (mặc định 2000) cho check-reputation và batch-public-info,
// User:BidTimeoutMs (mặc định 800) cho verify-bid-eligibility. Khi test local với DB từ xa có thể tăng, ví dụ 8000 và 3000.
TimeSpan? ReadTimeout(string key) =>
    int.TryParse(builder.Configuration[key], out var ms) && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null;

builder.Services.AddSingleton(new UserClientOptions(
    bypassUserCheck, ReadTimeout("User:TimeoutMs"), ReadTimeout("User:BidTimeoutMs")));
builder.Services.AddHttpClient<IUserClient, UserClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["User:BaseUrl"] ?? "http://localhost:8001");
    client.Timeout = TimeSpan.FromSeconds(30); // every call sets its own, shorter timeout (see UserClientOptions)
    client.DefaultRequestHeaders.Add("X-Caller-Service", "auction-service");
    if (!string.IsNullOrWhiteSpace(internalServiceKey))
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", internalServiceKey);
});

// Đồng bộ trạng thái sản phẩm sang Catalog (mở khoá khi phiên huỷ hoặc thất bại, chốt bán khi Seller chấp nhận đơn): hàng đợi trong bộ nhớ, gửi nền, thử lại khi lỗi.
builder.Services.AddSingleton<CatalogSyncQueue>();
builder.Services.AddSingleton<ICatalogSync>(sp => sp.GetRequiredService<CatalogSyncQueue>());
builder.Services.AddHostedService<CatalogSyncWorker>();

// Đẩy settlement sang Commerce để tạo đơn cho người thắng: hàng đợi trong bộ nhớ, gửi nền, thử lại khi lỗi. Mặc định tắt (Commerce:Enabled).
var commerceOptions = CommerceOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(commerceOptions);
builder.Services.AddHttpClient<ICommerceClient, CommerceClient>(client =>
{
    client.BaseAddress = new Uri(commerceOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(commerceOptions.TimeoutSeconds);
    client.DefaultRequestHeaders.Add("X-Caller-Service", "auction-service");
    if (!string.IsNullOrWhiteSpace(internalServiceKey))
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", internalServiceKey);
});
builder.Services.AddSingleton<CommerceSyncQueue>();
builder.Services.AddSingleton<ICommerceSync>(sp => sp.GetRequiredService<CommerceSyncQueue>());
builder.Services.AddHostedService<CommerceSyncWorker>();

// Thông báo sang Notification service: hàng đợi trong bộ nhớ, gửi nền, lỗi chỉ ghi log. Mặc định tắt (Notification:Enabled).
// Notification yêu cầu header x-user-id (GUID bất kỳ) và x-user-roles chứa "service" hoặc "admin".
var notificationOptions = NotificationOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(notificationOptions);
builder.Services.AddSingleton<NotificationClient>();
builder.Services.AddSingleton<INotificationClient>(sp => sp.GetRequiredService<NotificationClient>());
builder.Services.AddHttpClient(NotificationDispatcher.NotificationHttpClient, client =>
{
    client.BaseAddress = new Uri(notificationOptions.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("x-user-id", notificationOptions.ServiceUserId.ToString());
    client.DefaultRequestHeaders.Add("x-user-roles", "service");
    client.DefaultRequestHeaders.Add("X-Caller-Service", "auction-service");
});
builder.Services.AddHttpClient(NotificationDispatcher.UserHttpClient, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["User:BaseUrl"] ?? "http://localhost:8001");
    client.Timeout = TimeSpan.FromSeconds(5);
    client.DefaultRequestHeaders.Add("X-Caller-Service", "auction-service");
    if (!string.IsNullOrWhiteSpace(internalServiceKey))
        client.DefaultRequestHeaders.Add("X-Internal-Service-Key", internalServiceKey);
});
builder.Services.AddHostedService<NotificationDispatcher>();

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