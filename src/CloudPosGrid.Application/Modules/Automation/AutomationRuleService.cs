using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Automation;

/// <summary>Otomasyon kuralı görünüm modeli (tanım kaydı).</summary>
public record AutomationRuleDto(
    Guid Id, string Name, string TriggerType, string? ConditionJson,
    string ActionType, string? ActionConfigJson, bool IsActive, DateTime CreatedAt);

/// <summary>Yeni otomasyon kuralı oluşturma isteği.</summary>
public record CreateAutomationRuleRequest(
    string Name, string TriggerType, string? ConditionJson,
    string ActionType, string? ActionConfigJson, bool IsActive);

/// <summary>Mevcut otomasyon kuralı güncelleme isteği (tüm alanlar yeniden yazılır).</summary>
public record UpdateAutomationRuleRequest(
    string Name, string TriggerType, string? ConditionJson,
    string ActionType, string? ActionConfigJson, bool IsActive);

/// <summary>Kural etkinliğini aç/kapat isteği.</summary>
public record ToggleAutomationRuleRequest(bool Active);

/// <summary>Hazır kural şablonu (kullanıcıya tek tıkla kural kurma önerisi) — statik.</summary>
public record AutomationTemplate(string TriggerType, string ActionType, string Name, string Description);

public interface IAutomationRuleService
{
    /// <summary>Tüm otomasyon kurallarını (en yeni önce) listeler.</summary>
    Task<IReadOnlyList<AutomationRuleDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Yeni kural oluşturur (tetikleyici/eylem türü doğrulanır).</summary>
    Task<AutomationRuleDto> CreateAsync(CreateAutomationRuleRequest req, CancellationToken ct = default);

    /// <summary>Var olan kuralı günceller; yoksa <see cref="NotFoundException"/>.</summary>
    Task<AutomationRuleDto> UpdateAsync(Guid id, UpdateAutomationRuleRequest req, CancellationToken ct = default);

    /// <summary>Kuralı siler; yoksa <see cref="NotFoundException"/>.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Kuralı etkin/pasif yapar.</summary>
    Task<AutomationRuleDto> ToggleAsync(Guid id, bool active, CancellationToken ct = default);

    /// <summary>Hazır kural şablonlarını döndürür (statik öneri listesi).</summary>
    Task<IReadOnlyList<AutomationTemplate>> TemplatesAsync(CancellationToken ct = default);
}

/// <summary>
/// Kural motoru (if-this-then-that) TANIM servisi — CRUD + hazır şablonlar.
/// Kuralları çalıştıran taraf ayrıdır: <see cref="AutomationEngine"/>.
/// Tanımlar işletme geneldir (AutomationRule'da BranchId yoktur → şube izolasyonu yok).
/// </summary>
public sealed class AutomationRuleService : IAutomationRuleService
{
    private readonly IApplicationDbContext _db;

    public AutomationRuleService(IApplicationDbContext db) => _db = db;

    /// <summary>İzinli tetikleyici türleri (entity XML doc ile birebir).</summary>
    private static readonly HashSet<string> AllowedTriggers = new(StringComparer.Ordinal)
    { "low_stock", "overdue_receivable", "daily_summary", "appointment_soon" };

    /// <summary>İzinli eylem türleri (entity XML doc ile birebir).</summary>
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    { "notify", "sms", "email", "task" };

    /// <summary>Kullanıcıya sunulan hazır kural şablonları (statik).</summary>
    private static readonly IReadOnlyList<AutomationTemplate> Templates =
    [
        new AutomationTemplate("low_stock", "notify", "Düşük Stok Uyarısı",
            "Bir ürünün stoğu minimum seviyenin altına düştüğünde bildirim merkezine uyarı düşer."),
        new AutomationTemplate("overdue_receivable", "sms", "Geciken Alacak Hatırlatması",
            "Vadesi geçen cari alacaklar için müşteriye otomatik SMS hatırlatması gönderilir."),
        new AutomationTemplate("daily_summary", "email", "Günlük Özet E-postası",
            "Her gün gün sonunda satış ve kasa özeti e-posta ile gönderilir."),
        new AutomationTemplate("appointment_soon", "sms", "Yaklaşan Randevu Hatırlatması",
            "Randevu saatine yaklaşıldığında müşteriye SMS ile hatırlatma yapılır."),
        new AutomationTemplate("low_stock", "task", "Otomatik Sipariş Görevi",
            "Stok kritik seviyeye indiğinde tedarik siparişi için bir görev oluşturulur."),
    ];

    public async Task<IReadOnlyList<AutomationRuleDto>> ListAsync(CancellationToken ct = default)
    {
        return await _db.AutomationRules.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new AutomationRuleDto(
                r.Id, r.Name, r.TriggerType, r.ConditionJson,
                r.ActionType, r.ActionConfigJson, r.IsActive, r.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<AutomationRuleDto> CreateAsync(CreateAutomationRuleRequest req, CancellationToken ct = default)
    {
        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BusinessRuleException("Kural adı boş olamaz.");
        ValidateTrigger(req.TriggerType);
        ValidateAction(req.ActionType);
        ValidateJson(req.ConditionJson, "Koşul");
        ValidateJson(req.ActionConfigJson, "Eylem ayarı");

        var entity = new AutomationRule
        {
            Name = name,
            TriggerType = req.TriggerType,
            ConditionJson = req.ConditionJson,
            ActionType = req.ActionType,
            ActionConfigJson = req.ActionConfigJson,
            IsActive = req.IsActive,
        };
        _db.AutomationRules.Add(entity);
        await _db.SaveChangesAsync(ct);
        return Map(entity);
    }

    public async Task<AutomationRuleDto> UpdateAsync(Guid id, UpdateAutomationRuleRequest req, CancellationToken ct = default)
    {
        var rule = await _db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Otomasyon kuralı", id);

        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BusinessRuleException("Kural adı boş olamaz.");
        ValidateTrigger(req.TriggerType);
        ValidateAction(req.ActionType);
        ValidateJson(req.ConditionJson, "Koşul");
        ValidateJson(req.ActionConfigJson, "Eylem ayarı");

        rule.Name = name;
        rule.TriggerType = req.TriggerType;
        rule.ConditionJson = req.ConditionJson;
        rule.ActionType = req.ActionType;
        rule.ActionConfigJson = req.ActionConfigJson;
        rule.IsActive = req.IsActive;
        await _db.SaveChangesAsync(ct);
        return Map(rule);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rule = await _db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Otomasyon kuralı", id);
        _db.AutomationRules.Remove(rule);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<AutomationRuleDto> ToggleAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var rule = await _db.AutomationRules.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw NotFoundException.For("Otomasyon kuralı", id);
        rule.IsActive = active;
        await _db.SaveChangesAsync(ct);
        return Map(rule);
    }

    public Task<IReadOnlyList<AutomationTemplate>> TemplatesAsync(CancellationToken ct = default)
        => Task.FromResult(Templates);

    /// <summary>Serbest JSON alanları jsonb kolonuna yazılır: geçersiz metin Postgres'te 500'e düşerdi →
    /// kullanıcıya anlaşılır 400 döndür. Boş bırakmak serbesttir (opsiyonel alan).</summary>
    private static void ValidateJson(string? json, string label)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try { using var _ = System.Text.Json.JsonDocument.Parse(json); }
        catch (System.Text.Json.JsonException)
        {
            throw new BusinessRuleException(label + " geçerli bir JSON olmalı. Örnek: {\"threshold\": 10}");
        }
    }

    private static void ValidateTrigger(string triggerType)
    {
        if (!AllowedTriggers.Contains(triggerType ?? string.Empty))
            throw new BusinessRuleException("Geçersiz tetikleyici türü.");
    }

    private static void ValidateAction(string actionType)
    {
        if (!AllowedActions.Contains(actionType ?? string.Empty))
            throw new BusinessRuleException("Geçersiz eylem türü.");
    }

    private static AutomationRuleDto Map(AutomationRule r) => new(
        r.Id, r.Name, r.TriggerType, r.ConditionJson,
        r.ActionType, r.ActionConfigJson, r.IsActive, r.CreatedAt);
}
