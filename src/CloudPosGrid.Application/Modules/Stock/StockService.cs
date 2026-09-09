using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Stock;

public sealed class StockService : IStockService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditTrail _audit;
    private readonly ICurrentBranch _branch;

    public StockService(IApplicationDbContext db, ICurrentUser currentUser, IAuditTrail audit, ICurrentBranch branch)
    {
        _db = db;
        _currentUser = currentUser;
        _audit = audit;
        _branch = branch;
    }

    public async Task<StockMovementDto> CreateMovementAsync(CreateStockMovementRequest req, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == req.ProductId, ct)
            ?? throw NotFoundException.For("Ürün", req.ProductId);

        if (req.Type != StockMovementType.Adjustment && req.Quantity <= 0)
            throw new BusinessRuleException("Miktar 0'dan büyük olmalı.");

        // Çok-şube TAM stok: hareket AKTİF şubenin bakiyesine uygulanır.
        var branchId = await _branch.ResolveWriteBranchAsync(ct);
        var row = await BranchStockLedger.GetRowAsync(_db, product.Id, branchId, ct);

        var newBranchStock = req.Type switch
        {
            StockMovementType.In => row.Quantity + req.Quantity,
            StockMovementType.Out => row.Quantity - req.Quantity,
            StockMovementType.Adjustment => req.Quantity,
            _ => row.Quantity,
        };

        if (req.Type == StockMovementType.Out && req.Quantity > row.Quantity)
            throw new BusinessRuleException(
                $"Çıkış miktarı ({req.Quantity}) bu şubedeki stoktan ({row.Quantity}) fazla olamaz.");

        if (newBranchStock < 0)
            throw new BusinessRuleException("Stok negatif olamaz.");

        BranchStockLedger.ApplyDelta(row, product, newBranchStock - row.Quantity);

        var movement = new StockMovement
        {
            ProductId = product.Id,
            Type = req.Type,
            Quantity = req.Quantity,
            UnitCost = req.UnitCost ?? product.PurchasePrice,
            Reference = StockMovementReference.Manual,
            Note = req.Note,
            StockAfter = newBranchStock,
            BranchId = branchId,
            CreatedBy = _currentUser.UserId,
        };
        _db.StockMovements.Add(movement);
        await _db.SaveChangesAsync(ct);

        return new StockMovementDto(movement.Id, product.Id, product.Name, movement.Type,
            movement.Quantity, movement.UnitCost, movement.Reference, movement.Note,
            movement.StockAfter, movement.CreatedAt);
    }

    public async Task<StockMovementDto> RecordWasteAsync(RecordWasteRequest req, CancellationToken ct = default)
    {
        var product = await _db.Products.FirstOrDefaultAsync(p => p.Id == req.ProductId, ct)
            ?? throw NotFoundException.For("Ürün", req.ProductId);
        if (product.IsService)
            throw new BusinessRuleException("Hizmet kaleminde fire kaydı yapılamaz.");
        if (req.Quantity <= 0)
            throw new BusinessRuleException("Fire miktarı 0'dan büyük olmalı.");

        // Fire AKTİF şubenin stoğundan düşer (çok-şube tam stok).
        var branchId = await _branch.ResolveWriteBranchAsync(ct);
        var row = await BranchStockLedger.GetRowAsync(_db, product.Id, branchId, ct);
        if (req.Quantity > row.Quantity)
            throw new BusinessRuleException($"Fire miktarı ({req.Quantity}) bu şubedeki stoktan ({row.Quantity}) fazla olamaz.");

        BranchStockLedger.ApplyDelta(row, product, -req.Quantity);

        var label = req.Reason switch
        {
            WasteReason.Spoiled => "Bozuldu",
            WasteReason.Broken => "Kırıldı/hasar",
            WasteReason.Expired => "Son kullanma geçti",
            WasteReason.Complimentary => "İkram",
            _ => "Diğer",
        };
        var movement = new StockMovement
        {
            ProductId = product.Id,
            Type = StockMovementType.Out,
            Quantity = req.Quantity,
            UnitCost = product.PurchasePrice,       // fire maliyeti = alış maliyeti
            Reference = StockMovementReference.Waste,
            WasteReason = req.Reason,
            Note = string.IsNullOrWhiteSpace(req.Note) ? $"Fire: {label}" : $"Fire: {label} — {req.Note.Trim()}",
            StockAfter = row.Quantity,
            BranchId = branchId,
            CreatedBy = _currentUser.UserId,
        };
        _db.StockMovements.Add(movement);

        // Sorumluluk izi: fire stoğu azaltır ve maliyet yazar — kim, neyi, neden düştü?
        _audit.Record("StockWasteRecorded", "Product", product.Id,
            $"{product.Name} · {req.Quantity:0.##} {product.Unit} · {label} · {(req.Quantity * product.PurchasePrice):0.00} ₺");
        await _db.SaveChangesAsync(ct);

        return new StockMovementDto(movement.Id, product.Id, product.Name, movement.Type,
            movement.Quantity, movement.UnitCost, movement.Reference, movement.Note,
            movement.StockAfter, movement.CreatedAt);
    }

    public async Task<StockTransferResultDto> TransferAsync(StockTransferRequest req, CancellationToken ct = default)
    {
        if (req.FromBranchId == req.ToBranchId)
            throw new BusinessRuleException("Kaynak ve hedef şube aynı olamaz.");

        // Şube izolasyonu: kısıtlı kullanıcı (AllowedBranchIds dolu) YALNIZ izinli şubeleri arasında transfer yapabilir.
        // TransferAsync from/to'yu doğrudan istekten aldığı için (query filter yok) burada elle doğrula — aksi halde
        // şube-kilitli Admin başka şubenin stoğunu taşıyabilir / hata mesajından stok sızdırabilir. (Kısıtsız = Owner → serbest.)
        var allowed = _currentUser.AllowedBranchIds;
        if (allowed.Count > 0 && (!allowed.Contains(req.FromBranchId) || !allowed.Contains(req.ToBranchId)))
            throw new BusinessRuleException("Bu şubeler arasında transfer yetkiniz yok.");

        // Aynı ürün birden çok satırda gelirse topla; 0/negatifleri ele.
        var items = (req.Items ?? [])
            .GroupBy(i => i.ProductId)
            .Select(g => new StockTransferItem(g.Key, g.Sum(x => x.Quantity)))
            .Where(i => i.Quantity != 0)
            .ToList();
        if (items.Count == 0)
            throw new BusinessRuleException("Transfer edilecek ürün yok.");
        if (items.Any(i => i.Quantity < 0))
            throw new BusinessRuleException("Transfer miktarı negatif olamaz.");

        var from = await _db.Branches.FirstOrDefaultAsync(b => b.Id == req.FromBranchId, ct)
            ?? throw NotFoundException.For("Şube", req.FromBranchId);
        var to = await _db.Branches.FirstOrDefaultAsync(b => b.Id == req.ToBranchId, ct)
            ?? throw NotFoundException.For("Şube", req.ToBranchId);

        var ids = items.Select(i => i.ProductId).ToList();
        var products = await _db.Products
            .Where(p => ids.Contains(p.Id) && p.IsActive && !p.IsService)
            .ToDictionaryAsync(p => p.Id, ct);

        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        decimal totalQty = 0;
        foreach (var item in items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
                throw new BusinessRuleException("Transfer edilecek ürün bulunamadı veya stok takipli değil.");

            var fromRow = await BranchStockLedger.GetRowAsync(_db, product.Id, req.FromBranchId, ct);
            if (item.Quantity > fromRow.Quantity)
                throw new BusinessRuleException(
                    $"{product.Name}: {from.Name} şubesindeki stok ({fromRow.Quantity}) transfer miktarından ({item.Quantity}) az.");

            // Kaynaktan düş + hedefe ekle → Product.CurrentStock TOPLAMI net 0 değişir (sadece dağılım).
            BranchStockLedger.ApplyDelta(fromRow, product, -item.Quantity);
            var toRow = await BranchStockLedger.GetRowAsync(_db, product.Id, req.ToBranchId, ct);
            BranchStockLedger.ApplyDelta(toRow, product, item.Quantity);

            var transferNote = $"Transfer: {from.Name} → {to.Name}" + (note is null ? "" : $" ({note})");
            _db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id, Type = StockMovementType.Out, Quantity = item.Quantity,
                UnitCost = product.PurchasePrice, Reference = StockMovementReference.Transfer,
                Note = transferNote, StockAfter = fromRow.Quantity, BranchId = req.FromBranchId, CreatedBy = _currentUser.UserId,
            });
            _db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id, Type = StockMovementType.In, Quantity = item.Quantity,
                UnitCost = product.PurchasePrice, Reference = StockMovementReference.Transfer,
                Note = transferNote, StockAfter = toRow.Quantity, BranchId = req.ToBranchId, CreatedBy = _currentUser.UserId,
            });
            totalQty += item.Quantity;
        }

        // Sorumluluk izi: stok şubeler arası taşındı — kim, ne kadar, nereden nereye.
        _audit.Record("StockTransfer", "Stock", null, $"{items.Count} ürün, {totalQty} adet: {from.Name} → {to.Name}");
        await _db.SaveChangesAsync(ct);

        return new StockTransferResultDto(items.Count, totalQty, from.Name, to.Name);
    }

    public async Task<StockCountResultDto> ApplyStockCountAsync(ApplyStockCountRequest req, CancellationToken ct = default)
    {
        var rawItems = req.Items ?? [];
        if (rawItems.Count == 0)
            throw new BusinessRuleException("Sayılacak ürün yok.");

        // Aynı ürün listede birden çok kez gelirse SON değeri esas al (çift Adjustment hareketi / bozuk sayaç olmasın).
        var items = rawItems.GroupBy(i => i.ProductId).Select(g => g.Last()).ToList();
        var ids = items.Select(i => i.ProductId).ToList();
        // Yalnız aktif, stok-takipli (hizmet olmayan) ürünler sayılır.
        var products = await _db.Products
            .Where(p => ids.Contains(p.Id) && p.IsActive && !p.IsService)
            .ToDictionaryAsync(p => p.Id, ct);

        var note = string.IsNullOrWhiteSpace(req.Note) ? "Sayım düzeltmesi" : req.Note.Trim();
        var adjusted = 0;
        var eligible = 0;
        // Sayım AKTİF şubede yapılır → yalnız o şubenin bakiyesi sayılan değere ayarlanır (diğer şubeler etkilenmez).
        var branchId = await _branch.ResolveWriteBranchAsync(ct);

        foreach (var item in items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
                continue; // bulunamayan / pasif / hizmet kalemi → atla
            eligible++;
            var row = await BranchStockLedger.GetRowAsync(_db, product.Id, branchId, ct);
            if (row.Quantity == item.CountedQuantity)
                continue; // fark yok → hareket üretme (denetim gürültüsü olmasın)

            // Adjustment = ŞUBE stoğunu sayılan değere AYARLAR (delta değil); toplam da senkronlanır.
            BranchStockLedger.ApplyDelta(row, product, item.CountedQuantity - row.Quantity);
            _db.StockMovements.Add(new StockMovement
            {
                ProductId = product.Id,
                Type = StockMovementType.Adjustment,
                Quantity = item.CountedQuantity,
                UnitCost = product.PurchasePrice,
                Reference = StockMovementReference.Adjustment, // sayım hareketleri Manual'dan ayrışsın
                Note = note,
                StockAfter = item.CountedQuantity,
                BranchId = branchId,
                CreatedBy = _currentUser.UserId,
            });
            adjusted++;
        }

        // Açık taslak oturumunu KAPAT (Applied) → geçmiş kaydı olur. Yoksa (doğrudan uygula) geçmiş için aç.
        var session = await _db.StockCountSessions
            .Where(s => s.Status == StockCountStatus.Open && s.BranchId == branchId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (session is null)
        {
            session = new StockCountSession { BranchId = branchId, CreatedByUserId = _currentUser.UserId, CreatedByName = _currentUser.Email };
            _db.StockCountSessions.Add(session);
        }
        session.Status = StockCountStatus.Applied;
        session.AppliedAt = DateTime.UtcNow;
        session.CountedCount = items.Count;
        session.AdjustedCount = adjusted;

        if (adjusted > 0)
            // Sorumluluk izi: stok sayımı stoğu DOĞRUDAN değere ayarlar (fire/kayıp gizlenebilir) — kim uyguladı?
            _audit.Record("StockCountApplied", "Stock", null, $"{adjusted} üründe stok düzeltildi ({items.Count} okutuldu)");

        await _db.SaveChangesAsync(ct);

        return new StockCountResultDto(items.Count, adjusted, eligible - adjusted);
    }

    public async Task<StockCountSessionDto?> GetOpenCountSessionAsync(CancellationToken ct = default)
    {
        // Sayım şubeye özeldir: yalnız AKTİF şubenin açık taslağını döndür (başka şubenin/cihazın sayımı karışmasın).
        var branchId = await _branch.ResolveWriteBranchAsync(ct);
        var session = await _db.StockCountSessions
            .Include(s => s.Items)
            .Where(s => s.Status == StockCountStatus.Open && s.BranchId == branchId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return session is null ? null : await ToSessionDtoAsync(session, ct);
    }

    public async Task<StockCountSessionDto> SaveCountSessionAsync(SaveStockCountSessionRequest req, CancellationToken ct = default)
    {
        // Aynı ürün birden çok kez gelirse son değeri esas al (bozuk taslak olmasın).
        var items = (req.Items ?? []).GroupBy(i => i.ProductId).Select(g => g.Last()).ToList();
        var branchId = await _branch.ResolveWriteBranchAsync(ct);

        var session = await _db.StockCountSessions
            .Include(s => s.Items)
            .Where(s => s.Status == StockCountStatus.Open && s.BranchId == branchId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            session = new StockCountSession
            {
                Status = StockCountStatus.Open,
                BranchId = branchId,
                CreatedByUserId = _currentUser.UserId,
                CreatedByName = _currentUser.Email,
            };
            _db.StockCountSessions.Add(session);
        }
        else if (session.Items.Count > 0)
        {
            _db.StockCountSessionItems.RemoveRange(session.Items); // taslağı tümüyle değiştir (sil-ekle)
        }

        var ids = items.Select(i => i.ProductId).ToList();
        var stock = await EffectiveStockAsync(ids, ct);

        session.Items = items.Select(i => new StockCountSessionItem
        {
            ProductId = i.ProductId,
            CountedQuantity = i.CountedQuantity,
            SystemQuantitySnapshot = stock.TryGetValue(i.ProductId, out var s) ? s : 0m,
        }).ToList();
        session.CountedCount = session.Items.Count;

        await _db.SaveChangesAsync(ct);
        return await ToSessionDtoAsync(session, ct);
    }

    public async Task DiscardCountSessionAsync(CancellationToken ct = default)
    {
        var branchId = await _branch.ResolveWriteBranchAsync(ct);
        var session = await _db.StockCountSessions
            .Where(s => s.Status == StockCountStatus.Open && s.BranchId == branchId)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (session is null) return;
        session.Status = StockCountStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<StockCountHistoryDto>> GetCountHistoryAsync(int limit, CancellationToken ct = default)
    {
        // Şube izolasyonu: StockCountSession'da global query filter YOK (envanter analitiği tenant-geneli okuyor)
        // → geçmişi burada elle süz; kardeş metotlar zaten ResolveWriteBranch ile şubeye bağlı, yalnız geçmiş atlanmıştı.
        // Kilitli kullanıcıda HeaderBranchId kendi şubesine zorlanır (X-Branch-Id taklidi işe yaramaz);
        // kısıtsız kullanıcı başlık vermezse (br == null) tüm şubeler birleşik gelir.
        var br = _branch.HeaderBranchId;
        return await _db.StockCountSessions
            .Where(s => s.Status != StockCountStatus.Open)
            .Where(s => br == null || s.BranchId == br)
            .OrderByDescending(s => s.AppliedAt ?? s.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(s => new StockCountHistoryDto(
                s.Id, s.Status.ToString(), s.CreatedAt, s.AppliedAt, s.CreatedByName, s.CountedCount, s.AdjustedCount))
            .ToListAsync(ct);
    }

    private async Task<StockCountSessionDto> ToSessionDtoAsync(StockCountSession session, CancellationToken ct)
    {
        var ids = session.Items.Select(i => i.ProductId).ToList();
        var names = await _db.Products.Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name }).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var stock = await EffectiveStockAsync(ids, ct); // aktif şube stoğu (yoksa toplam) — sayım şubeye göre

        var items = session.Items.Select(i => new StockCountSessionItemDto(
            i.ProductId,
            names.GetValueOrDefault(i.ProductId, "(silinmiş ürün)"),
            i.CountedQuantity,
            stock.GetValueOrDefault(i.ProductId, 0m))).ToList();

        return new StockCountSessionDto(session.Id, session.Status.ToString(), session.CreatedAt, session.AppliedAt,
            session.CreatedByName, session.CountedCount, session.AdjustedCount, items);
    }

    /// <summary>Sayım için ürün başına ETKİN stok = YAZMA şubesinin bakiyesi (yoksa 0). Sayım daima somut bir
    /// şubeye uygulandığından (ResolveWriteBranch), snapshot/gösterim de o şubeyle EŞLEŞİR (okuma=yazma tutarlı).
    /// Başlık yoksa varsayılan şubeye düşer → "sistem" ile "uygula" aynı şubeyi gösterir (toplam değil).</summary>
    private async Task<Dictionary<Guid, decimal>> EffectiveStockAsync(List<Guid> ids, CancellationToken ct)
    {
        var branchId = await _branch.ResolveWriteBranchAsync(ct);
        return await _db.ProductBranchStocks
            .Where(x => x.BranchId == branchId && ids.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, ct);
    }

    public async Task<PagedResult<StockMovementDto>> GetMovementsAsync(StockMovementQuery query, CancellationToken ct = default)
    {
        var q = _db.StockMovements.AsQueryable();

        // Şube izolasyonu: StockMovement'ta global query filter YOK (tenant-geneli okuyan yerler de var)
        // → hareket geçmişini burada elle süz; aksi halde başka şubenin maliyeti (UnitCost) ve transfer notları sızar.
        var br = _branch.HeaderBranchId;
        q = q.Where(m => br == null || m.BranchId == br);

        if (query.ProductId is Guid pid) q = q.Where(m => m.ProductId == pid);
        if (query.Type is StockMovementType type) q = q.Where(m => m.Type == type);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(m => m.CreatedAt)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(StockMappings.ToMovementDto)
            .ToListAsync(ct);

        return new PagedResult<StockMovementDto>(items, total, query.Page, query.PageSize);
    }
}
