namespace Auction.Application.Services.AuctionConfig;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using global::Auction.Application.Common;
using global::Auction.Application.Common.Extensions;
using global::Auction.Application.Common.Models;
using global::Auction.Application.Interfaces;
using global::Auction.Domain.Enums;
using global::Auction.Domain.Exceptions;
using AuctionConfigEntity = global::Auction.Domain.Entities.AuctionConfig;

public sealed class AuctionConfigService : IAuctionConfigService
{
    private const string PolicyConfigKey = "DEFAULT_AUCTION_POLICY";
    private const string AntiSnipingMinutesName = "antiSnipingExtensionMinutes";
    private const string MaxExtensionCountName = "maxExtensionCount";
    private const string DefaultBidStepPercentName = "defaultBidStepPercent";

    private const int MinAntiSnipingMinutes = 0;
    private const int MaxAntiSnipingMinutes = 60;
    private const int MinExtensionCount = 0;
    private const int MaxExtensionCountLimit = 20;

    private readonly IAuctionDbContext _dbContext;
    private readonly TimeProvider _clock;

    /// <summary>Khởi tạo service với DbContext và đồng hồ hệ thống.</summary>
    public AuctionConfigService(IAuctionDbContext dbContext, TimeProvider clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    /// <summary>Lấy danh sách cấu hình đang áp dụng (chỉ version đang active), sắp xếp theo khoá cấu hình, có phân trang.</summary>
    public async Task<PagedData<AuctionConfigDto>> ListAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var query = _dbContext.AuctionConfigs
            .AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderBy(c => c.ConfigKey)
            .ThenByDescending(c => c.Version)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(ToDto).ToList();

        return new PagedData<AuctionConfigDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>
    /// Lấy lịch sử các phiên bản của một cấu hình theo khóa, sắp xếp version mới nhất trước và có phân trang.
    /// Mỗi phiên bản kèm danh sách trường thay đổi so với phiên bản liền trước.
    /// Phiên bản cũ nhất không có phiên bản trước nên danh sách thay đổi để trống.
    /// Trả lỗi 400 nếu khóa rỗng, 404 nếu chưa có cấu hình nào với khóa đó.
    /// </summary>
    public async Task<PagedData<AuctionConfigHistoryItemDto>> GetHistoryAsync(
        string configKey, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        var key = configKey?.Trim();
        if (string.IsNullOrEmpty(key))
            throw Invalid("VAL_INVALID_INPUT", "Config key is required.");

        var (safePage, safePageSize) = PaginationConstants.Normalize(page, pageSize);

        var query = _dbContext.AuctionConfigs
            .AsNoTracking()
            .Where(c => c.ConfigKey == key && !c.IsDeleted);

        var totalCount = await query.CountAsync(cancellationToken);
        if (totalCount == 0)
            throw new DomainException("CONFIG_NOT_FOUND", "The auction configuration was not found.", 404);

        // Lấy dư một dòng để dòng cuối của trang vẫn có phiên bản liền trước để so sánh.
        var rows = await query
            .OrderByDescending(c => c.Version)
            .ThenByDescending(c => c.CreatedAt)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize + 1)
            .ToListAsync(cancellationToken);

        var items = rows
            .Take(safePageSize)
            .Select((row, index) => new AuctionConfigHistoryItemDto(
                row.Id,
                row.Version,
                row.IsActive,
                ParseValue(row.ConfigValue),
                index + 1 < rows.Count ? DiffValues(rows[index + 1].ConfigValue, row.ConfigValue) : Array.Empty<AuctionConfigChangeDto>(),
                row.UpdatedBy,
                row.UpdatedAt))
            .ToList();

        return new PagedData<AuctionConfigHistoryItemDto>(items, PaginationMetadata.Create(safePage, safePageSize, totalCount));
    }

    /// <summary>
    /// Phát hành một version mới của cấu hình chống bắn tỉa. Kiểm tra số phút (0 đến 60) và số lần gia hạn (0 đến 20); các khoá khác trong JSON được giữ nguyên.
    /// Version cũ chuyển IsActive = false làm lịch sử, version mới có Version + 1. Giá trị không đổi thì không tạo version mới và không ghi audit.
    /// Hai admin cập nhật cùng lúc thì người sau nhận 409 CONCURRENT_UPDATE.
    /// </summary>
    public async Task<AuctionConfigDto> UpdateAsync(
        ActorContext actor, UpdateAuctionConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (request.AntiSnipingExtensionMinutes is null && request.MaxExtensionCount is null &&
            request.DefaultBidStepPercent is null)
            throw Invalid("VAL_NO_FIELDS_TO_UPDATE", "At least one field must be provided.");

        if (request.DefaultBidStepPercent is { } percent &&
            (percent <= 0m || percent > 100m || percent != decimal.Round(percent, 2)))
            throw Invalid(
                "VAL_INVALID_DEFAULT_BID_STEP_PERCENT",
                "Default bid step percent must be greater than 0 and at most 100, with at most two decimal places.");

        if (request.AntiSnipingExtensionMinutes is { } minutes &&
            (minutes < MinAntiSnipingMinutes || minutes > MaxAntiSnipingMinutes))
            throw Invalid(
                "VAL_INVALID_ANTI_SNIPING_MINUTES",
                $"Anti-sniping extension minutes must be between {MinAntiSnipingMinutes} and {MaxAntiSnipingMinutes}.");

        if (request.MaxExtensionCount is { } count &&
            (count < MinExtensionCount || count > MaxExtensionCountLimit))
            throw Invalid(
                "VAL_INVALID_MAX_EXTENSION_COUNT",
                $"Maximum extension count must be between {MinExtensionCount} and {MaxExtensionCountLimit}.");

        var config = await _dbContext.AuctionConfigs
            .Where(c => c.ConfigKey == PolicyConfigKey && c.IsActive && !c.IsDeleted)
            .OrderByDescending(c => c.Version)
            .FirstOrDefaultAsync(cancellationToken);

        if (config is null)
            throw new DomainException("CONFIG_NOT_FOUND", "The default auction policy configuration was not found.", 404);

        var policy = JsonNode.Parse(config.ConfigValue) as JsonObject
            ?? throw new InvalidOperationException("Auction policy configuration is invalid.");

        var oldValue = config.ConfigValue;

        var changed = false;
        if (request.AntiSnipingExtensionMinutes is { } newMinutes)
            changed |= SetInt(policy, AntiSnipingMinutesName, newMinutes);
        if (request.MaxExtensionCount is { } newCount)
            changed |= SetInt(policy, MaxExtensionCountName, newCount);
        if (request.DefaultBidStepPercent is { } newPercent)
            changed |= SetDecimal(policy, DefaultBidStepPercentName, newPercent);

        // Nothing changes, so no new version and no audit entry are created.
        if (!changed)
            return ToDto(config);

        var now = _clock.GetUtcNow();
        var newConfig = new AuctionConfigEntity
        {
            Id = Guid.NewGuid(),
            ConfigKey = config.ConfigKey,
            ConfigValue = policy.ToJsonString(),
            Description = config.Description,
            Version = config.Version + 1,
            IsActive = true,
            UpdatedBy = actor.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        // The old version stays as inactive history. IsActive is a concurrency token, so if another admin
        // published a newer version first, saving fails and there are never two active versions.
        config.IsActive = false;
        _dbContext.AuctionConfigs.Add(newConfig);

        _dbContext.AddAuditLog(
            AuditAction.ConfigUpdate, "AuctionConfig", newConfig.Id.ToString(),
            oldValues: new { ConfigId = config.Id, config.ConfigKey, ConfigValue = oldValue, config.Version },
            newValues: new { ConfigId = newConfig.Id, newConfig.ConfigKey, newConfig.ConfigValue, newConfig.Version },
            userId: actor.UserId.ToString(), ipAddress: actor.ClientIp, traceId: actor.TraceId);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DomainException(
                "CONCURRENT_UPDATE", "The configuration was changed by another request. Reload it and try again.", 409);
        }

        return ToDto(newConfig);
    }

    /// <summary>Gán một thuộc tính số thập phân vào JSON cấu hình và trả về true nếu giá trị thực sự thay đổi.</summary>
    private static bool SetDecimal(JsonObject policy, string name, decimal value)
    {
        decimal? current = policy[name] is JsonValue node && node.TryGetValue<decimal>(out var existing) ? existing : null;
        if (current == value)
            return false;

        policy[name] = value;
        return true;
    }

    /// <summary>Gán một thuộc tính số nguyên vào JSON cấu hình và trả về true nếu giá trị thực sự thay đổi.</summary>
    private static bool SetInt(JsonObject policy, string name, int value)
    {
        int? current = policy[name] is JsonValue node && node.TryGetValue<int>(out var existing) ? existing : null;
        if (current == value)
            return false;

        policy[name] = value;
        return true;
    }

    /// <summary>So sánh hai giá trị JSON của cấu hình theo từng trường cấp một và trả về các trường khác nhau (thêm, xóa hoặc đổi giá trị).</summary>
    private static IReadOnlyList<AuctionConfigChangeDto> DiffValues(string oldRaw, string newRaw)
    {
        var oldFields = ReadFields(oldRaw);
        var newFields = ReadFields(newRaw);

        var changes = new List<AuctionConfigChangeDto>();
        foreach (var name in oldFields.Keys.Union(newFields.Keys).OrderBy(n => n, StringComparer.Ordinal))
        {
            var hasOld = oldFields.TryGetValue(name, out var oldValue);
            var hasNew = newFields.TryGetValue(name, out var newValue);

            if (hasOld && hasNew && oldValue.GetRawText() == newValue.GetRawText())
                continue;

            changes.Add(new AuctionConfigChangeDto(
                name,
                hasOld ? (JsonElement?)oldValue : null,
                hasNew ? (JsonElement?)newValue : null));
        }

        return changes;
    }

    /// <summary>Đọc các trường cấp một của một chuỗi JSON object. Chuỗi không hợp lệ hoặc không phải object trả về tập rỗng.</summary>
    private static Dictionary<string, JsonElement> ReadFields(string raw)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    fields[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException)
        {
            // Giá trị không phải JSON hợp lệ thì coi như không có trường nào.
        }

        return fields;
    }

    /// <summary>Đổi thực thể cấu hình thành DTO trả về cho client (giá trị JSON được parse thành đối tượng).</summary>
    private static AuctionConfigDto ToDto(AuctionConfigEntity c) => new(
        c.Id, c.ConfigKey, ParseValue(c.ConfigValue), c.Description, c.Version, c.IsActive, c.UpdatedBy, c.UpdatedAt);

    /// <summary>Parse giá trị JSON của cấu hình. Chuỗi không phải JSON hợp lệ thì trả về dưới dạng chuỗi thường.</summary>
    private static JsonElement ParseValue(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(raw);
        }
    }

    /// <summary>Tạo lỗi nghiệp vụ 400 với mã lỗi và thông điệp cho trước.</summary>
    private static DomainException Invalid(string errorCode, string message) => new(errorCode, message, 400);
}