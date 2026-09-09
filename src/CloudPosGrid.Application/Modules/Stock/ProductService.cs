using System.Text.Json;
using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Common;
using CloudPosGrid.Domain.Entities;
using CloudPosGrid.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Modules.Stock;

public sealed class ProductService : IProductService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IPlanEntitlementProvider _entitlements;
    private readonly ICurrentBranch _branch;
    private readonly IAuditTrail _audit;

    public ProductService(IApplicationDbContext db, ICurrentUser currentUser, IPlanEntitlementProvider entitlements,
        ICurrentBranch branch, IAuditTrail audit)
    {
        _db = db;
        _currentUser = currentUser;
        _entitlements = entitlements;
        _branch = branch;
        _audit = audit;
    }

    /// <summary>Şube seçiliyse (HeaderBranchId) her DTO'ya o şubenin stoğunu (BranchStock) doldurur.
    /// Şube seçili değilse ("tüm şubeler"/kısıtsız) BranchStock null kalır → istemci CurrentStock toplamını gösterir.</summary>
    private async Task<List<ProductDto>> FillBranchStockAsync(List<ProductDto> dtos, CancellationToken ct)
    {
        if (_branch.HeaderBranchId is not Guid branchId || dtos.Count == 0) return dtos;
        var ids = dtos.Select(d => d.Id).ToList();
        var stock = await _db.ProductBranchStocks
            .Where(x => x.BranchId == branchId && ids.Contains(x.ProductId))
            .ToDictionaryAsync(x => x.ProductId, x => x.Quantity, ct);
        return dtos.Select(d => d with { BranchStock = stock.GetValueOrDefault(d.Id, 0m) }).ToList();
    }

    public async Task<PagedResult<ProductDto>> GetAsync(ProductQuery query, CancellationToken ct = default)
    {
        var q = _db.Products.AsQueryable();

        if (!query.IncludeInactive) q = q.Where(p => p.IsActive);
        if (query.CategoryId is Guid cid) q = q.Where(p => p.CategoryId == cid);
        // ŞUBE: kritik-stok süzgeci seçili şubenin bakiyesine bakar (satırda gösterilen BranchStock ile aynı
        // sayı) — zincir toplamına bakarsa şubede tükenmiş ürün listede çıkmaz, kullanıcı çelişki görür.
        if (query.LowStock == true)
        {
            var lowBr = _branch.HeaderBranchId;
            q = lowBr is Guid lb
                ? q.Where(p => (_db.ProductBranchStocks.Where(s => s.ProductId == p.Id && s.BranchId == lb)
                        .Sum(s => (decimal?)s.Quantity) ?? 0m) <= p.MinStock)
                : q.Where(p => p.CurrentStock <= p.MinStock);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = SqlLike.Contains(query.Search);
            q = q.Where(p =>
                EF.Functions.ILike(p.Name, pattern) ||
                EF.Functions.ILike(p.Sku, pattern) ||
                (p.Barcode != null && EF.Functions.ILike(p.Barcode, pattern)));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderBy(p => p.Name)
            .Skip(query.Skip).Take(query.PageSize)
            .Select(StockMappings.ToProductDto)
            .ToListAsync(ct);

        return new PagedResult<ProductDto>(await FillBranchStockAsync(items, ct), total, query.Page, query.PageSize);
    }

    public async Task<ProductDto> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var dto = await _db.Products.Where(p => p.Id == id).Select(StockMappings.ToProductDto).FirstOrDefaultAsync(ct)
            ?? throw NotFoundException.For("Ürün", id);
        return (await FillBranchStockAsync([dto], ct))[0];
    }

    public async Task<ProductDto?> GetByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        var dto = await _db.Products.Where(p => p.IsActive && p.Barcode == barcode)
            .Select(StockMappings.ToProductDto).FirstOrDefaultAsync(ct);
        return dto is null ? null : (await FillBranchStockAsync([dto], ct))[0];
    }

    public async Task<ScanResultDto?> ScanAsync(string code, CancellationToken ct = default)
    {
        var c = code?.Trim();
        if (string.IsNullOrEmpty(c)) return null;

        var s = await _db.Settings.FirstOrDefaultAsync(ct);
        var tpl = new ScaleBarcodeTemplate(
            s?.ScaleBarcodeEnabled ?? false, s?.ScaleBarcodePrefixes ?? "",
            s?.ScaleBarcodeItemDigits ?? 5, s?.ScaleBarcodeValueDigits ?? 5, s?.ScaleBarcodeDecimals ?? 3,
            s?.ScaleBarcodeEmbeds ?? ScaleEmbedMode.Weight, s?.ScaleBarcodePriceIncludesVat ?? true);

        if (ScaleBarcode.TryParse(c, tpl) is { } parts)
        {
            var dto = await _db.Products.Where(p => p.IsActive && p.ScaleItemCode == parts.ItemCode)
                .Select(StockMappings.ToProductDto).FirstOrDefaultAsync(ct);
            if (dto is not null)
            {
                dto = (await FillBranchStockAsync([dto], ct))[0];
                if (parts.Value <= 0m)
                    throw new BusinessRuleException("Terazi barkodunda değer okunamadı (0). Etiketi tekrar okutun.");

                if (tpl.Embeds == ScaleEmbedMode.Weight)
                    return new ScanResultDto(dto, parts.Value, null, true);

                // Fiyat modu: terazi genelde KDV DAHİL raf tutarını basar → satır birim fiyatı net'e çevrilir.
                var unit = tpl.PriceIncludesVat && dto.VatRate > 0m
                    ? parts.Value / (1 + dto.VatRate / 100m)
                    : parts.Value;
                return new ScanResultDto(dto, 1m, Math.Round(unit, 2), true);
            }
            // Şablona uyuyor ama o kodda ürün yok → normal barkod olarak da denenir (aşağıya düşer).
        }

        var normal = await GetByBarcodeAsync(c, ct);
        return normal is null ? null : new ScanResultDto(normal, 1m, null, false);
    }

    public async Task<ProductDto> CreateAsync(CreateProductRequest req, CancellationToken ct = default)
    {
        // Deneme sürümü ürün sınırı (plan yetkilerinden).
        var ent = await _entitlements.GetAsync(ct);
        if (ent.MaxProducts is int max && await _db.Products.CountAsync(p => p.IsActive && !p.IsVariantParent, ct) >= max)
            throw new PlanUpgradeException($"Deneme sürümünde en fazla {max} ürün ekleyebilirsiniz. Sınırsız ürün için paketinizi yükseltin.");

        var sku = string.IsNullOrWhiteSpace(req.Sku)
            ? "PRD-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()
            : req.Sku.Trim();

        if (await _db.Products.AnyAsync(p => p.Sku == sku, ct))
            throw new ConflictException($"Bu stok kodu (SKU) zaten kullanılıyor: {sku}");

        if (req.CategoryId is Guid cid && !await _db.Categories.AnyAsync(c => c.Id == cid, ct))
            throw NotFoundException.For("Kategori", cid);

        var product = new Product
        {
            Sku = sku,
            Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim(),
            Name = req.Name.Trim(),
            CategoryId = req.CategoryId,
            Unit = string.IsNullOrWhiteSpace(req.Unit) ? "adet" : req.Unit.Trim(),
            PurchasePrice = req.PurchasePrice,
            SalePrice = req.SalePrice,
            VatRate = req.VatRate,
            MinStock = req.MinStock,
            CurrentStock = 0,
            IsActive = true,
            IsService = req.IsService,
            ExpiryDate = req.ExpiryDate,
            PurchaseUnit = req.PurchaseUnit,
            PurchaseUnitFactor = req.PurchaseUnitFactor,
            ImageUrl = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl.Trim(),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
            IsVisibleOnMenu = req.IsVisibleOnMenu,
            MenuSortOrder = req.MenuSortOrder,
            ShelfLocation = Clean(req.ShelfLocation),
            StorageArea = Clean(req.StorageArea),
            BrandName = Clean(req.BrandName),
            ImageUrls = NormalizeImages(req.ImageUrls),
            DimensionalWeight = req.DimensionalWeight is { } dw && dw > 0m ? dw : null,
            ScaleItemCode = await UniqueScaleCodeAsync(Clean(req.ScaleItemCode), null, ct),
        };
        _db.Products.Add(product);

        // Hizmet kalemleri stok takip etmez -> açılış stoğu yok sayılır.
        if (!product.IsService && req.OpeningStock > 0)
        {
            // Açılış stoğu AKTİF şubeye girer (çok-şube tam stok); Product.CurrentStock toplamı senkronlanır.
            var branchId = await _branch.ResolveWriteBranchAsync(ct);
            await BranchStockLedger.AdjustAsync(_db, product, branchId, req.OpeningStock, ct);
            _db.StockMovements.Add(new StockMovement
            {
                Product = product,
                ProductId = product.Id,
                Type = StockMovementType.In,
                Quantity = req.OpeningStock,
                UnitCost = req.PurchasePrice,
                Reference = StockMovementReference.Manual,
                Note = "Açılış stoğu",
                StockAfter = req.OpeningStock,
                BranchId = branchId,
                CreatedBy = _currentUser.UserId,
            });
        }

        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(product.Id, ct);
    }

    public async Task<ProductWithVariantsDto> CreateWithVariantsAsync(CreateProductWithVariantsRequest req, CancellationToken ct = default)
    {
        var variants = req.Variants ?? [];
        if (variants.Count == 0)
            throw new BusinessRuleException("En az bir varyant gerekli.");
        if (variants.Count > 200)
            throw new BusinessRuleException("Tek seferde en fazla 200 varyant oluşturulabilir.");
        if (string.IsNullOrWhiteSpace(req.Name))
            throw new BusinessRuleException("Ürün adı gerekli.");

        // Deneme sınırı: satılabilir SKU'lar (varyantlar) sayılır; parent şablon sayılmaz.
        var ent = await _entitlements.GetAsync(ct);
        if (ent.MaxProducts is int max)
        {
            var current = await _db.Products.CountAsync(p => p.IsActive && !p.IsVariantParent, ct);
            if (current + variants.Count > max)
                throw new PlanUpgradeException($"Deneme sürümünde en fazla {max} ürün ekleyebilirsiniz. Sınırsız ürün için paketinizi yükseltin.");
        }

        if (req.CategoryId is Guid cid && !await _db.Categories.AnyAsync(c => c.Id == cid, ct))
            throw NotFoundException.For("Kategori", cid);

        // Benzersizlik: mevcut + bu partide üretilen SKU/barkodları tek kümede rezerve et.
        var skus = (await _db.Products.Select(p => p.Sku).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var barcodes = (await _db.Products.Where(p => p.Barcode != null).Select(p => p.Barcode!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var name = req.Name.Trim();
        var unit = string.IsNullOrWhiteSpace(req.Unit) ? "adet" : req.Unit.Trim();
        var image = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl.Trim();
        var brand = Clean(req.BrandName);

        var parent = new Product
        {
            Sku = UniqueSku(skus),
            Name = name,
            CategoryId = req.CategoryId,
            Unit = unit,
            PurchasePrice = req.PurchasePrice,
            SalePrice = req.SalePrice,
            VatRate = req.VatRate,
            MinStock = req.MinStock,
            CurrentStock = 0,
            IsActive = true,
            IsService = false,
            IsVariantParent = true,
            VariantAttributesJson = JsonSerializer.Serialize(req.Attributes ?? new List<VariantAttributeDef>()),
            Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description!.Trim(),
            ImageUrl = image,
            BrandName = brand,
            IsVisibleOnMenu = req.IsVisibleOnMenu,
        };
        _db.Products.Add(parent);

        Guid? writeBranch = null;
        var created = new List<Product>();
        foreach (var v in variants)
        {
            if (string.IsNullOrWhiteSpace(v.Label))
                throw new BusinessRuleException("Varyant etiketi boş olamaz.");

            string sku;
            if (string.IsNullOrWhiteSpace(v.Sku)) sku = UniqueSku(skus);
            else { sku = v.Sku.Trim(); if (!skus.Add(sku)) throw new ConflictException($"Bu stok kodu (SKU) zaten kullanılıyor: {sku}"); }

            string barcode;
            if (string.IsNullOrWhiteSpace(v.Barcode)) barcode = UniqueBarcode(barcodes);
            else { barcode = v.Barcode.Trim(); if (!barcodes.Add(barcode)) throw new ConflictException($"Bu barkod zaten kullanılıyor: {barcode}"); }

            var variant = new Product
            {
                Sku = sku,
                Barcode = barcode,
                Name = $"{name} — {v.Label.Trim()}",
                CategoryId = req.CategoryId,
                Unit = unit,
                PurchasePrice = v.PurchasePrice ?? req.PurchasePrice,
                SalePrice = v.SalePrice ?? req.SalePrice,
                VatRate = req.VatRate,
                MinStock = v.MinStock ?? req.MinStock,
                CurrentStock = 0,
                IsActive = true,
                IsService = false,
                ParentProductId = parent.Id,
                VariantValues = v.Label.Trim(),
                ImageUrl = image,
                BrandName = brand,
                IsVisibleOnMenu = req.IsVisibleOnMenu,
            };
            _db.Products.Add(variant);
            created.Add(variant);

            if (v.OpeningStock > 0)
            {
                writeBranch ??= await _branch.ResolveWriteBranchAsync(ct);
                await BranchStockLedger.AdjustAsync(_db, variant, writeBranch.Value, v.OpeningStock, ct);
                _db.StockMovements.Add(new StockMovement
                {
                    Product = variant,
                    ProductId = variant.Id,
                    Type = StockMovementType.In,
                    Quantity = v.OpeningStock,
                    UnitCost = variant.PurchasePrice,
                    Reference = StockMovementReference.Manual,
                    Note = "Açılış stoğu",
                    StockAfter = v.OpeningStock,
                    BranchId = writeBranch.Value,
                    CreatedBy = _currentUser.UserId,
                });
            }
        }

        await _db.SaveChangesAsync(ct);

        var parentDto = await GetByIdAsync(parent.Id, ct);
        var variantDtos = new List<ProductDto>();
        foreach (var v in created)
            variantDtos.Add(await GetByIdAsync(v.Id, ct));
        return new ProductWithVariantsDto(parentDto, variantDtos);
    }

    public async Task<BulkPriceResultDto> BulkUpdatePricesAsync(BulkPriceUpdateRequest req, CancellationToken ct = default)
    {
        var isPurchase = string.Equals(req.Target, "Purchase", StringComparison.OrdinalIgnoreCase);
        var isPercent = !string.Equals(req.Mode, "Amount", StringComparison.OrdinalIgnoreCase);
        if (isPercent && req.Value <= -100m)
            throw new BusinessRuleException("Yüzde indirim −100'den küçük olamaz.");
        if (req.Value == 0m)
            throw new BusinessRuleException("Değişim miktarı 0 olamaz.");

        var q = _db.Products.Where(p => p.IsActive);
        if (isPurchase) q = q.Where(p => !p.IsService);           // hizmetin alış fiyatı anlamsız
        if (req.CategoryId is Guid cid) q = q.Where(p => p.CategoryId == cid);
        if (req.ProductIds is { Count: > 0 } ids) q = q.Where(p => ids.Contains(p.Id));

        var list = await q.OrderBy(p => p.Name).ToListAsync(ct);
        var sample = new List<BulkPricePreviewItem>();
        var affected = 0;

        foreach (var p in list)
        {
            var oldPrice = isPurchase ? p.PurchasePrice : p.SalePrice;
            var raw = isPercent ? oldPrice * (1m + req.Value / 100m) : oldPrice + req.Value;
            var neu = Round(Math.Max(0m, raw), req.Rounding);
            if (neu == oldPrice) continue;

            if (sample.Count < 5) sample.Add(new BulkPricePreviewItem(p.Id, p.Name, oldPrice, neu));
            if (!req.Preview)
            {
                if (isPurchase) p.PurchasePrice = neu; else p.SalePrice = neu;
            }
            affected++;
        }

        if (!req.Preview && affected > 0)
        {
            // Sorumluluk izi: toplu fiyat değişimi geniş etkili — kim, neyi, ne kadar değiştirdi?
            var scope = req.ProductIds is { Count: > 0 } s ? $"{s.Count} seçili ürün"
                : req.CategoryId is not null ? "kategori" : "tüm ürünler";
            var change = isPercent ? $"%{req.Value:0.##}" : $"{req.Value:0.00} ₺";
            _audit.Record("BulkPriceUpdate", "Product", null,
                $"{(isPurchase ? "Alış" : "Satış")} fiyatı {change} · {scope} · {affected} ürün güncellendi");
            await _db.SaveChangesAsync(ct);
        }

        return new BulkPriceResultDto(affected, !req.Preview && affected > 0, sample);
    }

    /// <summary>Psikolojik yuvarlama: Ninety → x,90 · Fifty → x,50 · Whole → tam sayı · diğer → 2 hane.</summary>
    private static decimal Round(decimal v, string? mode) => (mode ?? "").ToLowerInvariant() switch
    {
        "ninety" => Math.Max(0m, Math.Round(v - 0.90m, 0, MidpointRounding.AwayFromZero) + 0.90m),
        "fifty" => Math.Round(v * 2m, 0, MidpointRounding.AwayFromZero) / 2m,
        "whole" => Math.Round(v, 0, MidpointRounding.AwayFromZero),
        _ => Math.Round(v, 2, MidpointRounding.AwayFromZero),
    };

    /// <summary>Kümede olmayan benzersiz SKU üretir ve kümeye ekler (parti-içi çakışmayı da önler).</summary>
    private static string UniqueSku(HashSet<string> taken)
    {
        string sku;
        do { sku = "PRD-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(); }
        while (!taken.Add(sku));
        return sku;
    }

    /// <summary>Kümede olmayan benzersiz dahili EAN-13 üretir ve kümeye ekler.</summary>
    private static string UniqueBarcode(HashSet<string> taken)
    {
        string bc;
        do { bc = GenerateInternalEan13(); }
        while (!taken.Add(bc));
        return bc;
    }

    public async Task<ImportResultDto> ImportAsync(ImportProductsRequest req, CancellationToken ct = default)
    {
        var rows = req.Rows ?? [];
        if (rows.Count == 0) throw new BusinessRuleException("İçe aktarılacak satır bulunamadı.");
        if (rows.Count > 5000) throw new BusinessRuleException("Tek seferde en fazla 5000 satır aktarılabilir.");

        var ent = await _entitlements.GetAsync(ct);
        var currentCount = await _db.Products.CountAsync(p => p.IsActive && !p.IsVariantParent, ct);
        var remainingSlots = ent.MaxProducts is int max ? Math.Max(0, max - currentCount) : int.MaxValue;

        // 1. Faz: kategorileri ada göre bul; eksikleri oluştur ve kaydet (böylece Id'ler kesinleşir).
        var catByName = (await _db.Categories.ToListAsync(ct))
            .GroupBy(c => c.Name.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var anyNewCat = false;
        foreach (var cn in rows.Select(r => r.CategoryName?.Trim()).Where(n => !string.IsNullOrWhiteSpace(n))
                     .Select(n => n!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = cn.ToLowerInvariant();
            if (!catByName.ContainsKey(key))
            {
                var cat = new Category { Name = cn };
                _db.Categories.Add(cat);
                catByName[key] = cat;
                anyNewCat = true;
            }
        }
        if (anyNewCat) await _db.SaveChangesAsync(ct);

        // 2. Faz: satırları doğrula ve ürünleri oluştur.
        var existingSkus = (await _db.Products.Select(p => p.Sku).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var existingBarcodes = (await _db.Products.Where(p => p.Barcode != null).Select(p => p.Barcode!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenSku = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBarcode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var errors = new List<ImportRowError>();
        var added = 0;
        var rowNum = 0;
        Guid? importBranchId = null; // açılış stoğu için yazma şubesi (ilk gerekende bir kez çözülür)

        foreach (var r in rows)
        {
            rowNum++;
            var name = r.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { errors.Add(new(rowNum, name ?? "", "Ürün adı boş.")); continue; }
            if (r.SalePrice is not > 0) { errors.Add(new(rowNum, name, "Satış fiyatı 0'dan büyük olmalı.")); continue; }
            // Aralık doğrulaması: bozuk CSV/kopyala-yapıştır değerleri sessizce kabul edilmesin.
            if (r.SalePrice > 1_000_000m) { errors.Add(new(rowNum, name, "Satış fiyatı çok yüksek (en fazla 1.000.000).")); continue; }
            if (r.VatRate is < 0m or > 100m) { errors.Add(new(rowNum, name, "KDV oranı 0-100 arasında olmalı.")); continue; }
            if (r.PurchasePrice is < 0m) { errors.Add(new(rowNum, name, "Alış fiyatı negatif olamaz.")); continue; }
            if (r.OpeningStock is < 0m) { errors.Add(new(rowNum, name, "Açılış stoğu negatif olamaz.")); continue; }
            if (r.MinStock is < 0m) { errors.Add(new(rowNum, name, "Minimum stok negatif olamaz.")); continue; }
            if (added >= remainingSlots) { errors.Add(new(rowNum, name, $"Plan ürün sınırına ({ent.MaxProducts}) ulaşıldı — yükseltin.")); continue; }

            var sku = string.IsNullOrWhiteSpace(r.Sku) ? "PRD-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant() : r.Sku.Trim();
            if (existingSkus.Contains(sku) || !seenSku.Add(sku)) { errors.Add(new(rowNum, name, $"SKU zaten mevcut: {sku}")); continue; }

            string? barcode = string.IsNullOrWhiteSpace(r.Barcode) ? null : r.Barcode.Trim();
            if (barcode is not null && (existingBarcodes.Contains(barcode) || !seenBarcode.Add(barcode))) { errors.Add(new(rowNum, name, $"Barkod zaten mevcut: {barcode}")); continue; }

            Guid? categoryId = null;
            var catName = r.CategoryName?.Trim();
            if (!string.IsNullOrWhiteSpace(catName) && catByName.TryGetValue(catName.ToLowerInvariant(), out var cat))
                categoryId = cat.Id;

            var isService = r.IsService ?? false;
            var opening = r.OpeningStock ?? 0m;
            var product = new Product
            {
                Sku = sku,
                Barcode = barcode,
                Name = name,
                CategoryId = categoryId,
                Unit = string.IsNullOrWhiteSpace(r.Unit) ? "adet" : r.Unit.Trim(),
                PurchasePrice = r.PurchasePrice ?? 0m,
                SalePrice = r.SalePrice!.Value,
                VatRate = r.VatRate ?? 0m,
                MinStock = r.MinStock ?? 0m,
                CurrentStock = 0m,
                IsActive = true,
                IsService = isService,
                IsVisibleOnMenu = true,
                MenuSortOrder = 0,
            };
            _db.Products.Add(product);

            if (!isService && opening > 0)
            {
                importBranchId ??= await _branch.ResolveWriteBranchAsync(ct);
                await BranchStockLedger.AdjustAsync(_db, product, importBranchId.Value, opening, ct);
                _db.StockMovements.Add(new StockMovement
                {
                    Product = product,
                    ProductId = product.Id,
                    Type = StockMovementType.In,
                    Quantity = opening,
                    UnitCost = product.PurchasePrice,
                    Reference = StockMovementReference.Manual,
                    Note = "İçe aktarma açılış stoğu",
                    StockAfter = opening,
                    BranchId = importBranchId.Value,
                    CreatedBy = _currentUser.UserId,
                });
            }
            added++;
        }

        if (added > 0) await _db.SaveChangesAsync(ct);
        return new ImportResultDto(added, errors.Count, errors);
    }

    public async Task<ProductDto> UpdateAsync(Guid id, UpdateProductRequest req, CancellationToken ct = default)
    {
        var product = await _db.Products.FindAsync([id], ct)
            ?? throw NotFoundException.For("Ürün", id);

        var sku = req.Sku.Trim();
        if (!string.Equals(product.Sku, sku, StringComparison.OrdinalIgnoreCase) &&
            await _db.Products.AnyAsync(p => p.Sku == sku && p.Id != id, ct))
            throw new ConflictException($"Bu stok kodu (SKU) zaten kullanılıyor: {sku}");

        if (req.CategoryId is Guid cid && !await _db.Categories.AnyAsync(c => c.Id == cid, ct))
            throw NotFoundException.For("Kategori", cid);

        product.Sku = sku;
        product.Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        product.Name = req.Name.Trim();
        product.CategoryId = req.CategoryId;
        product.Unit = string.IsNullOrWhiteSpace(req.Unit) ? "adet" : req.Unit.Trim();
        product.PurchasePrice = req.PurchasePrice;
        product.SalePrice = req.SalePrice;
        product.VatRate = req.VatRate;
        product.MinStock = req.MinStock;
        product.IsActive = req.IsActive;
        product.IsService = req.IsService;
        product.ExpiryDate = req.ExpiryDate;
        product.PurchaseUnit = req.PurchaseUnit;
        product.PurchaseUnitFactor = req.PurchaseUnitFactor;
        product.ImageUrl = string.IsNullOrWhiteSpace(req.ImageUrl) ? null : req.ImageUrl.Trim();
        product.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
        product.IsVisibleOnMenu = req.IsVisibleOnMenu;
        product.MenuSortOrder = req.MenuSortOrder;
        product.ShelfLocation = Clean(req.ShelfLocation);
        product.StorageArea = Clean(req.StorageArea);
        product.BrandName = Clean(req.BrandName);
        product.ImageUrls = NormalizeImages(req.ImageUrls);
        product.DimensionalWeight = req.DimensionalWeight is { } dw && dw > 0m ? dw : null;
        product.ScaleItemCode = await UniqueScaleCodeAsync(Clean(req.ScaleItemCode), product.Id, ct);
        // Not: CurrentStock yalnızca stok hareketleriyle değişir.

        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<ProductDto> GenerateBarcodeAsync(Guid id, CancellationToken ct = default)
    {
        var product = await _db.Products.FindAsync([id], ct)
            ?? throw NotFoundException.For("Ürün", id);

        // Zaten barkodu varsa dokunma (idempotent — düğmeye tekrar basmak zarar vermesin).
        if (!string.IsNullOrWhiteSpace(product.Barcode))
            return await GetByIdAsync(id, ct);

        // Benzersiz dahili EAN-13 üret (SKU otomatik-üretimi gibi: üret → çakışma kontrol → gerekirse tekrar).
        var barcode = "";
        var unique = false;
        for (var attempt = 0; attempt < 25 && !unique; attempt++)
        {
            barcode = GenerateInternalEan13();
            unique = !await _db.Products.AnyAsync(p => p.Barcode == barcode, ct);
        }
        if (!unique)
            throw new ConflictException("Benzersiz barkod üretilemedi, lütfen tekrar deneyin.");

        product.Barcode = barcode;
        await _db.SaveChangesAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    /// <summary>
    /// Dahili kullanım için EAN-13 aday barkodu üretir: prefix "20" + 10 rastgele hane + kontrol hanesi.
    /// "2" GS1'in perakende iç-kullanım aralığıdır (gerçek üretici barkodlarıyla çakışmaz); ikinci hane
    /// "0"a SABİTLENİR ki TERAZİ ön ekleriyle (21-29) çakışmasın — aksi halde rastgele üretilen bir
    /// "28…" barkodu terazi etiketi sanılıp rastgele haneler ağırlık olarak okunurdu.
    /// </summary>
    private static string GenerateInternalEan13()
    {
        Span<char> digits = stackalloc char[13];
        digits[0] = '2';
        digits[1] = '0';
        for (var i = 2; i < 12; i++)
            digits[i] = (char)('0' + Random.Shared.Next(10));
        digits[12] = ScaleBarcode.CheckDigit(digits[..12]); // kontrol hanesi tek kaynaktan
        return new string(digits);
    }

    /// <summary>Terazi ürün kodunu doğrular: yalnız rakam ve başka üründe kullanılmıyor olmalı.
    /// İki ürün aynı kodu taşırsa etiket okutmada yanlış ürün satılır — bu yüzden sert hata.</summary>
    private async Task<string?> UniqueScaleCodeAsync(string? code, Guid? selfId, CancellationToken ct)
    {
        if (code is null) return null;
        if (code.Length > 8 || code.Any(ch => ch is < '0' or > '9'))
            throw new BusinessRuleException("Terazi ürün kodu yalnız rakamlardan oluşmalı (en fazla 8 hane).");
        if (await _db.Products.AnyAsync(p => p.ScaleItemCode == code && (selfId == null || p.Id != selfId), ct))
            throw new ConflictException($"Bu terazi ürün kodu başka bir üründe kullanılıyor: {code}");
        return code;
    }

    /// <summary>Boş/whitespace girdiyi null'a çevirir, aksi halde trim'ler (raf/bölge gibi opsiyonel alanlar).</summary>
    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Ek görsel listesini temizler: boşları at, kırp, tekilleştir (en fazla 8 — pazaryeri sınırı).</summary>
    private static List<string> NormalizeImages(List<string>? urls)
        => (urls ?? new()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).Distinct().Take(8).ToList();

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var product = await _db.Products.FindAsync([id], ct)
            ?? throw NotFoundException.For("Ürün", id);

        // Hareket geçmişini korumak için soft-delete.
        product.IsActive = false;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Kritik stok listesi (zil + panel). Varyant parent'ları stoksuz şablondur (stok varyantlarda)
    /// → uyarıya dahil edilmez. ŞUBE seçiliyse o şubenin bakiyesi kıyaslanır: şube kullanıcısının sipariş
    /// kararı kendi rafına göre verilmeli (zincir toplamı yüksekken şubede bitmiş ürün gözden kaçmasın).</summary>
    public async Task<List<ProductDto>> GetLowStockAsync(CancellationToken ct = default)
    {
        var br = _branch.HeaderBranchId;
        if (br is Guid lb)
            return await _db.Products
                .Where(p => p.IsActive && !p.IsVariantParent
                    && (_db.ProductBranchStocks.Where(s => s.ProductId == p.Id && s.BranchId == lb)
                            .Sum(s => (decimal?)s.Quantity) ?? 0m) <= p.MinStock)
                .OrderBy(p => p.CurrentStock)
                .Select(StockMappings.ToProductDto)
                .ToListAsync(ct);

        return await _db.Products
            .Where(p => p.IsActive && !p.IsVariantParent && p.CurrentStock <= p.MinStock)
            .OrderBy(p => p.CurrentStock)
            .Select(StockMappings.ToProductDto)
            .ToListAsync(ct);
    }

    public async Task<List<StockMovementDto>> GetProductMovementsAsync(Guid productId, CancellationToken ct = default)
    {
        if (!await _db.Products.AnyAsync(p => p.Id == productId, ct))
            throw NotFoundException.For("Ürün", productId);

        // Şube izolasyonu: StockMovement'ta global query filter YOK (tenant-geneli okuyan yerler de var)
        // → ürün hareket geçmişini burada elle süz; aksi halde başka şubenin maliyeti ve transfer notları sızar.
        // Şube seçili değilse (kısıtsız kullanıcı, başlık yok) tüm şubeler birleşik gelir.
        var br = _branch.HeaderBranchId;
        return await _db.StockMovements
            .Where(m => m.ProductId == productId)
            .Where(m => br == null || m.BranchId == br)
            .OrderByDescending(m => m.CreatedAt)
            .Select(StockMappings.ToMovementDto)
            .ToListAsync(ct);
    }
}
