namespace Auction.Infrastructure.Notifications;

using System;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Auction.Application.Interfaces;

/// <summary>
/// Đọc hàng đợi và gửi từng thông báo sang Notification service ở nền.
/// Bị vượt giá: POST /api/v1/notifications/auction-outbids. Người thắng: POST /api/v1/notifications/auction-winners (cần email người thắng, tra từ User service).
/// Mã sự kiện (sourceEventId) được sinh cố định từ dữ liệu nghiệp vụ nên gửi lại cùng thông báo là an toàn: Notification trả lại kết quả cũ, không gửi trùng.
/// Lỗi tạm thời (5xx, 408, 429, hết giờ chờ, mất kết nối) thử lại tối đa NotificationOptions.MaxAttempts lần; lỗi 4xx khác chỉ ghi log. Không có outbox nên thông báo cuối cùng thất bại sẽ bị bỏ.
/// </summary>
public sealed class NotificationDispatcher : BackgroundService
{
    /// <summary>Tên HttpClient gọi Notification service (đăng ký trong Program.cs).</summary>
    public const string NotificationHttpClient = "notification-service";

    /// <summary>Tên HttpClient gọi User service để lấy email người thắng (đăng ký trong Program.cs).</summary>
    public const string UserHttpClient = "notification-user-contact";

    private const string OutbidPath = "/api/v1/notifications/auction-outbids";
    private const string WinnerPath = "/api/v1/notifications/auction-winners";

    // Endpoint đề xuất bên User service (hiện chưa có): nhận { "userId": guid }, trả email và fullName (có thể nằm trong "data").
    private const string UserContactPath = "/api/v1/internal/users/contact-info";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NotificationClient _client;
    private readonly IHttpClientFactory _httpFactory;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotificationDispatcher> _logger;

    public NotificationDispatcher(
        NotificationClient client, IHttpClientFactory httpFactory, NotificationOptions options, ILogger<NotificationDispatcher> logger)
    {
        _client = client;
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Notifications are disabled (Notification:Enabled is false).");
            return;
        }

        _logger.LogInformation("Notification dispatcher started. Notification service: {BaseUrl}.", _options.BaseUrl);

        try
        {
            await foreach (var item in _client.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await DeliverAsync(item, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not deliver a notification.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Service đang tắt.
        }
    }

    private async Task DeliverAsync(NotificationWorkItem item, CancellationToken cancellationToken)
    {
        switch (item)
        {
            case BidOutbidWorkItem outbid:
                await PostWithRetryAsync(
                    OutbidPath, BuildOutbid(outbid.Notification), "bid outbid", outbid.Notification.AuctionId, cancellationToken);
                break;

            case AuctionWinnerWorkItem winner:
            {
                var contact = await ResolveWinnerContactAsync(winner.Notification.WinnerUserId, cancellationToken);
                if (contact is null)
                {
                    _logger.LogWarning(
                        "The winner notification of auction {AuctionId} was skipped: the email of user {UserId} is not available.",
                        winner.Notification.AuctionId, winner.Notification.WinnerUserId);
                    return;
                }

                await PostWithRetryAsync(
                    WinnerPath, BuildWinner(winner.Notification, contact), "auction winner", winner.Notification.AuctionId, cancellationToken);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ Request bodies

    private static object BuildOutbid(BidOutbidNotification n) => new
    {
        sourceEventId = DeterministicGuid($"auction-outbid:{n.TriggerBidId:N}"),
        auctionId = n.AuctionId,
        triggerBidId = n.TriggerBidId,
        previousBidId = n.PreviousBidId,
        previousHighestBidderUserId = n.PreviousHighestBidderUserId,
        newHighestBidderUserId = n.NewHighestBidderUserId,
        sellerUserId = n.SellerUserId,
        auctionTitle = CleanTitle(n.AuctionTitle),
        previousAmount = n.PreviousAmount,
        newAmount = n.NewAmount,
        currency = "VND",
        occurredAt = n.OccurredAt,
        auctionEndsAt = n.AuctionEndsAt,
        traceId = Clip(n.TraceId, 100)
    };

    private static object BuildWinner(AuctionWinnerNotification n, WinnerContact contact) => new
    {
        sourceEventId = DeterministicGuid($"auction-winner:{n.AuctionId:N}:{n.ResultId:N}"),
        auctionId = n.AuctionId,
        resultId = n.ResultId,
        winningBidId = n.WinningBidId,
        winnerUserId = n.WinnerUserId,
        sellerUserId = n.SellerUserId,
        winnerEmail = contact.Email,
        winnerFullName = Clip(contact.FullName, 200),
        auctionTitle = CleanTitle(n.AuctionTitle),
        winningAmount = n.WinningAmount,
        currency = "VND",
        settledAt = n.SettledAt,
        paymentDeadline = n.PaymentDeadline
    };

    // ------------------------------------------------------------------ HTTP

    /// <summary>Gửi một request JSON, thử lại khi lỗi tạm thời. Không bao giờ ném lỗi nghiệp vụ ra ngoài (chỉ ghi log); chỉ ném khi service đang tắt.</summary>
    private async Task PostWithRetryAsync(string path, object body, string kind, Guid auctionId, CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(NotificationHttpClient);
        var json = JsonSerializer.Serialize(body, JsonOptions);

        for (var attempt = 1; attempt <= NotificationOptions.MaxAttempts; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(path, content, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("The {Kind} notification of auction {AuctionId} was accepted by the Notification service.", kind, auctionId);
                    return;
                }

                var status = (int)response.StatusCode;
                var detail = await ReadSnippetAsync(response, cancellationToken);
                var temporary = status >= 500 || status == 408 || status == 429;
                if (!temporary)
                {
                    _logger.LogWarning(
                        "The Notification service rejected the {Kind} notification of auction {AuctionId} (HTTP {Status}): {Detail}",
                        kind, auctionId, status, detail);
                    return;
                }

                _logger.LogWarning(
                    "The Notification service answered HTTP {Status} to the {Kind} notification of auction {AuctionId} (attempt {Attempt}): {Detail}",
                    status, kind, auctionId, attempt, detail);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    "The {Kind} notification of auction {AuctionId} could not be sent (attempt {Attempt}): {Message}",
                    kind, auctionId, attempt, ex.Message);
            }

            if (attempt < NotificationOptions.MaxAttempts)
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), cancellationToken);
        }

        _logger.LogError(
            "The {Kind} notification of auction {AuctionId} was dropped after {Attempts} attempts.",
            kind, auctionId, NotificationOptions.MaxAttempts);
    }

    /// <summary>Lấy email và tên người thắng. Có DevWinnerEmail thì dùng luôn; không thì hỏi User service. Không lấy được thì trả null.</summary>
    private async Task<WinnerContact?> ResolveWinnerContactAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_options.DevWinnerEmail is not null)
            return new WinnerContact(_options.DevWinnerEmail, null);

        try
        {
            var http = _httpFactory.CreateClient(UserHttpClient);
            using var content = new StringContent(JsonSerializer.Serialize(new { userId }, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(UserContactPath, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("The User service answered HTTP {Status} when asked for the contact of user {UserId}.", (int)response.StatusCode, userId);
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object)
            {
                root = data;
            }

            var email = ReadString(root, "email");
            return string.IsNullOrWhiteSpace(email) ? null : new WinnerContact(email.Trim(), ReadString(root, "fullName"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning("Could not get the contact of user {UserId}: {Message}", userId, ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ Helpers

    private sealed record WinnerContact(string Email, string? FullName);

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static async Task<string> ReadSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length <= 300 ? text : text[..300];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>Mã sự kiện cố định sinh từ một chuỗi, để gửi lại cùng thông báo luôn có cùng sourceEventId.</summary>
    private static Guid DeterministicGuid(string name) => new(MD5.HashData(Encoding.UTF8.GetBytes(name)));

    /// <summary>Tiêu đề phiên theo yêu cầu của Notification: không rỗng, không có ký tự điều khiển, tối đa 200 ký tự.</summary>
    private static string CleanTitle(string? title)
    {
        var cleaned = new string((title ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return "Auction";

        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
