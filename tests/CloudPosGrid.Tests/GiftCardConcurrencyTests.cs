using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Tests;

/// <summary>
/// Hediye çeki eşzamanlılığı (#3/#7 jeton boşluğu). GiftCard.Balance PARA taşır ve redeem'de
/// okuma-değiştirme-yazma yapılır: paralel iki harcama aynı bakiyeyi okur, ikisi de "Active" +
/// tutar&lt;=bakiye kontrolünü geçer → 100 TL'lik çek iki kez harcanır. Diğer para yollarının aksine
/// redeem başka jetonlu bir kaydı (kasa/cari/ürün) yazmaz, yani onu serileştiren bir şey yoktu.
/// Bu testler (a) xmin jetonunun modele bağlandığını, (b) gerçek Postgres'te araya giren güncellemenin
/// ikinci yazmayı geri aldığını, (c) sıralı fazla/tekrar harcamanın iş kuralıyla reddedildiğini doğrular.
/// </summary>
[Collection("api")]
public class GiftCardConcurrencyTests
{
    private readonly ApiFixture _fx;
    public GiftCardConcurrencyTests(ApiFixture fx) => _fx = fx;

    private static int _seq;
    private static string NewEmail() => $"gcc{Interlocked.Increment(ref _seq)}_{Guid.NewGuid():N}@test.local";

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r, string method, string url)
    {
        var txt = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Xunit.Sdk.XunitException($"{method} {url} -> {(int)r.StatusCode}\n{txt}");
        return string.IsNullOrWhiteSpace(txt) ? default : JsonDocument.Parse(txt).RootElement.Clone();
    }
    private static async Task<JsonElement> Post(HttpClient c, string url, object body) => await ReadAsync(await c.PostAsJsonAsync(url, body), "POST", url);
    private static async Task<JsonElement> Get(HttpClient c, string url) => await ReadAsync(await c.GetAsync(url), "GET", url);

    /// <summary>Kayıt + Kurumsal'a yükseltme (hediye çeki MarketingTools kilidi altında); tenantId de gerekir.</summary>
    private async Task<(HttpClient Client, Guid TenantId)> RegisterEnterpriseAsync()
    {
        var email = NewEmail();
        await _fx.SeedVerificationAsync(email, "111111");
        var c = _fx.Factory.CreateClient();
        var res = await Post(c, "/api/auth/register", new
        {
            companyName = "Hediye Çeki İşletme", fullName = "Sahip", email, password = "test1234", businessType = "Retail", code = "111111",
        });
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", res.GetProperty("accessToken").GetString());
        var tenantId = Guid.Parse(res.GetProperty("user").GetProperty("tenantId").GetString()!);
        await _fx.ActivateEnterpriseAsync(tenantId);
        return (c, tenantId);
    }

    private async Task<string> SchemaOfAsync(Guid tenantId)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var master = scope.ServiceProvider.GetRequiredService<IMasterDbContext>();
        return await master.Tenants.Where(t => t.Id == tenantId).Select(t => t.SchemaName).FirstAsync();
    }

    /// <summary>HTTP dışında tenant şemasına bağlı taze bir scope: search_path interceptor'ı ITenantContext'ten okur.</summary>
    private IServiceScope NewTenantScope(Guid tenantId, string schema)
    {
        var scope = _fx.Factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, schema);
        return scope;
    }

    private static async Task<string> IssueCardAsync(HttpClient c, decimal initialBalance)
    {
        var card = await Post(c, "/api/gift-cards", new
        {
            initialBalance, contactId = (string?)null, expiresAt = (string?)null, note = "eşzamanlılık testi",
        });
        return card.GetProperty("code").GetString()!;
    }

    /// <summary>Para taşıyan diğer varlıklarla (Product/CashAccount/Contact/Order) aynı desen modele bağlanmış olmalı.</summary>
    [Fact]
    public void GiftCard_is_configured_with_the_xmin_optimistic_concurrency_token()
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var entity = db.Model.FindEntityType(typeof(GiftCard));
        Assert.NotNull(entity);

        var tokens = entity!.GetProperties().Where(p => p.IsConcurrencyToken).Select(p => p.Name).ToList();
        Assert.True(
            tokens.Contains("xmin"),
            $"GiftCard'da xmin iyimser eşzamanlılık jetonu yok (bulunanlar: [{string.Join(", ", tokens)}]) — çift harcama açık kalır.");
    }

    /// <summary>
    /// Redeem'in kritik aralığı (ikisi de OKUR → ikisi de YAZAR) iki ayrı DbContext/bağlantı ile birebir
    /// kurgulanır ve GERÇEK Postgres'e uygulanır. Jeton olmasa ikinci SaveChanges da geçerdi = 100 TL iki kez
    /// harcanırdı. Yarış HTTP üzerinden zamanlanmıyor: iki eşzamanlı isteğin kritik aralıkta gerçekten
    /// örtüşeceği garanti edilemez (test yeşil kalıp hiçbir şey kanıtlamayabilir) — bu yüzden araya girme
    /// deterministik olarak elle sıralanıyor; doğrulanan şey aynı: satır jetonu ikinci yazmayı geri alır.
    /// </summary>
    [Fact]
    public async Task Interleaved_double_redeem_of_the_same_card_is_rejected_by_the_token()
    {
        var (c, tenantId) = await RegisterEnterpriseAsync();
        var code = await IssueCardAsync(c, 100m);
        var schema = await SchemaOfAsync(tenantId);

        using var scopeA = NewTenantScope(tenantId, schema);
        using var scopeB = NewTenantScope(tenantId, schema);
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<AppDbContext>();

        // İkisi de Balance=100 / Status="Active" okur — servisteki kontroller ikisinde de geçerdi.
        var cardA = await dbA.GiftCards.FirstAsync(g => g.Code == code);
        var cardB = await dbB.GiftCards.FirstAsync(g => g.Code == code);
        Assert.Equal(100m, cardA.Balance);
        Assert.Equal(100m, cardB.Balance);

        cardA.Balance -= 100m;
        cardA.Status = "Used";
        await dbA.SaveChangesAsync(); // ilk harcama geçer

        cardB.Balance -= 100m;
        cardB.Status = "Used";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () => await dbB.SaveChangesAsync());

        // Bakiye yalnız BİR harcama kadar düştü; ikinci harcama hiç yazılmadı (para kaybı yok).
        var after = await Get(c, $"/api/gift-cards/{code}");
        Assert.Equal(0m, after.GetProperty("balance").GetDecimal());
        Assert.Equal("Used", after.GetProperty("status").GetString());
    }

    /// <summary>Sıralı (yarışsız) yol regresyonu: fazla ve tekrar harcama reddedilir, bakiye asla eksiye düşmez.</summary>
    [Fact]
    public async Task Over_and_repeat_redeem_are_rejected_and_balance_never_goes_negative()
    {
        var (c, _) = await RegisterEnterpriseAsync();
        var code = await IssueCardAsync(c, 50m);

        // Bakiyeden büyük harcama → 400; bakiye dokunulmadan kalır.
        var over = await c.PostAsJsonAsync($"/api/gift-cards/{code}/redeem", new { amount = 80m });
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        Assert.Equal(50m, (await Get(c, $"/api/gift-cards/{code}")).GetProperty("balance").GetDecimal());

        // Tam bakiye harcaması → bakiye 0, durum "Used".
        var used = await Post(c, $"/api/gift-cards/{code}/redeem", new { amount = 50m });
        Assert.Equal(0m, used.GetProperty("balance").GetDecimal());
        Assert.Equal("Used", used.GetProperty("status").GetString());

        // Aynı çeki tekrar harcama → 400 (aktif değil); bakiye eksiye düşmez.
        var again = await c.PostAsJsonAsync($"/api/gift-cards/{code}/redeem", new { amount = 50m });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        var final = await Get(c, $"/api/gift-cards/{code}");
        Assert.Equal(0m, final.GetProperty("balance").GetDecimal());
        Assert.Equal("Used", final.GetProperty("status").GetString());
    }
}
