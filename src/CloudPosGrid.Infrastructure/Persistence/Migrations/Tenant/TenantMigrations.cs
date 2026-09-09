namespace CloudPosGrid.Infrastructure.Persistence.Migrations.Tenant;

/// <summary>
/// Tenant şemalarına uygulanacak sürümlü, idempotent SQL göçleri.
///
/// Yeni tenant'lar <c>GenerateCreateScript()</c> ile en güncel şemayla kurulduğundan
/// <see cref="TenantSchemaMigrator.BaselineAsync"/> ile tüm göçler "uygulanmış" sayılır.
/// Mevcut tenant'lar ise açılışta <see cref="TenantSchemaMigrator.ApplyAsync"/> ile
/// bu listedeki eksik göçleri sırayla alır. Böylece model değişince şemalar drift etmez.
///
/// Kural: her göç idempotent olmalı ve listeye yalnızca SONA eklenmeli (Id'ler sabit kalır).
/// </summary>
public static class TenantMigrations
{
    public record Migration(string Id, string Sql);

    /// <summary>Trigram (pg_trgm) arama indeksleri — ILIKE '%...%' aramalarını hızlandırır.
    /// Hem 0008 göçünde (mevcut tenant'lar) hem de TenantProvisioner'da (yeni tenant'lar) kullanılır.
    /// Uzantı public şemada; indeksler geçerli tenant şemasındaki tablolarda oluşur.</summary>
    public const string TrgmSql =
        """
        CREATE EXTENSION IF NOT EXISTS pg_trgm SCHEMA public;
        CREATE INDEX IF NOT EXISTS ix_products_name_trgm ON products USING gin ("Name" gin_trgm_ops);
        CREATE INDEX IF NOT EXISTS ix_contacts_name_trgm ON contacts USING gin ("Name" gin_trgm_ops);
        """;

    public static readonly IReadOnlyList<Migration> All =
    [
        // invoices."Number" artık benzersiz olmalı (atomik fatura no üretimiyle uyumlu).
        // Not: kolon adları PascalCase olduğundan çift tırnakla ("Number") referans veriliyor.
        // Eski (unique olmayan) index adını bilmeden bulup kaldırır, sonra benzersizini kurar.
        new Migration("0001_invoices_number_unique",
            """
            DO $$
            DECLARE idx text;
            BEGIN
                SELECT indexname INTO idx FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND tablename = 'invoices'
                  AND indexdef ILIKE '%"number"%'
                  AND indexdef NOT ILIKE '%unique%';
                IF idx IS NOT NULL THEN
                    EXECUTE format('DROP INDEX %I', idx);
                END IF;
            END $$;
            CREATE UNIQUE INDEX IF NOT EXISTS ix_invoices_number_unique ON invoices ("Number");
            """),

        // Hizmet kalemi (stok takipsiz) için products."IsService" kolonu.
        new Migration("0002_products_is_service",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "IsService" boolean NOT NULL DEFAULT false;
            """),

        // Adisyon sistemi: bölgeler, masalar, adisyonlar ve satırları.
        // Kolon adları EF modeliyle (PascalCase) birebir; yeni tenant'lar GenerateCreateScript ile,
        // mevcut tenant'lar bu DDL ile aynı şemaya kavuşur.
        new Migration("0003_orders",
            """
            CREATE TABLE IF NOT EXISTS service_areas (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(120) NOT NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );

            CREATE TABLE IF NOT EXISTS dining_tables (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(80) NOT NULL,
                "AreaId" uuid NULL REFERENCES service_areas ("Id") ON DELETE SET NULL,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_dining_tables_AreaId" ON dining_tables ("AreaId");

            CREATE TABLE IF NOT EXISTS orders (
                "Id" uuid PRIMARY KEY,
                "Type" varchar(20) NOT NULL,
                "Status" varchar(20) NOT NULL,
                "TableId" uuid NULL REFERENCES dining_tables ("Id") ON DELETE SET NULL,
                "ContactId" uuid NULL,
                "Label" varchar(120) NULL,
                "Note" varchar(500) NULL,
                "OpenedAt" timestamp without time zone NOT NULL,
                "ClosedAt" timestamp without time zone NULL,
                "OpenedByUserId" uuid NULL,
                "InvoiceId" uuid NULL,
                "Subtotal" numeric(18,4) NOT NULL DEFAULT 0,
                "VatTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "GrandTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_orders_Status" ON orders ("Status");
            CREATE INDEX IF NOT EXISTS "IX_orders_TableId" ON orders ("TableId");

            CREATE TABLE IF NOT EXISTS order_lines (
                "Id" uuid PRIMARY KEY,
                "OrderId" uuid NOT NULL REFERENCES orders ("Id") ON DELETE CASCADE,
                "ProductId" uuid NOT NULL REFERENCES products ("Id") ON DELETE RESTRICT,
                "ProductName" varchar(200) NOT NULL,
                "Quantity" numeric(18,4) NOT NULL,
                "UnitPrice" numeric(18,4) NOT NULL,
                "VatRate" numeric(18,4) NOT NULL,
                "LineTotal" numeric(18,4) NOT NULL,
                "Note" varchar(300) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_order_lines_OrderId" ON order_lines ("OrderId");
            CREATE INDEX IF NOT EXISTS "IX_order_lines_ProductId" ON order_lines ("ProductId");
            """),

        // Ürün medya + menü alanları (fotoğraf, açıklama, QR menü görünürlüğü/sırası).
        new Migration("0004_product_media",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "ImageUrl" varchar(500) NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "Description" varchar(1000) NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "IsVisibleOnMenu" boolean NOT NULL DEFAULT true;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "MenuSortOrder" integer NOT NULL DEFAULT 0;
            """),

        // Adisyon kaynağı (POS / QR) — QR menüden gelen siparişleri ayırt etmek için.
        new Migration("0005_order_source",
            """
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS "Source" varchar(20) NOT NULL DEFAULT 'Pos';
            """),

        // Servis iş emri alanları: araç/cihaz bilgisi + iş akışı durumu.
        new Migration("0006_order_service",
            """
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS "AssetInfo" varchar(120) NULL;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS "WorkStatus" varchar(20) NULL;
            """),

        // Randevu modülü (kuaför/güzellik).
        new Migration("0007_appointments",
            """
            CREATE TABLE IF NOT EXISTS appointments (
                "Id" uuid PRIMARY KEY,
                "CustomerName" varchar(200) NOT NULL,
                "Phone" varchar(40) NULL,
                "ServiceName" varchar(200) NULL,
                "StartsAt" timestamp without time zone NOT NULL,
                "DurationMinutes" integer NOT NULL DEFAULT 30,
                "Status" varchar(20) NOT NULL,
                "Price" numeric(18,4) NOT NULL DEFAULT 0,
                "Note" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_appointments_StartsAt" ON appointments ("StartsAt");
            """),

        // Trigram arama indeksleri (pg_trgm) — ürün/cari adında ILIKE aramalarını hızlandırır.
        new Migration("0008_trgm_search_indexes", TrgmSql),

        // Müşteriye özel indirim yüzdesi.
        new Migration("0009_contact_discount",
            """
            ALTER TABLE contacts ADD COLUMN IF NOT EXISTS "DiscountRate" numeric(18,4) NOT NULL DEFAULT 0;
            """),

        // Randevuya bağlı hizmet ürünleri (tahsilatta satış faturası bunlardan oluşturulur).
        new Migration("0010_appointment_products",
            """
            ALTER TABLE appointments ADD COLUMN IF NOT EXISTS "ProductIds" varchar(500) NULL;
            """),

        // Çok şube (hafif): branches tablosu + kasa/satış/gelir-gider/masa/randevu tablolarına BranchId.
        // Varsayılan "Merkez" şubesi oluşturulur ve mevcut tüm kayıtlar ona bağlanır (BranchId dolar).
        new Migration("0011_branches",
            """
            CREATE TABLE IF NOT EXISTS branches (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(120) NOT NULL,
                "Address" varchar(300) NULL,
                "Phone" varchar(40) NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "IsDefault" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );

            ALTER TABLE cash_accounts ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            ALTER TABLE orders ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            ALTER TABLE finance_transactions ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            ALTER TABLE dining_tables ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            ALTER TABLE service_areas ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;
            ALTER TABLE appointments ADD COLUMN IF NOT EXISTS "BranchId" uuid NULL;

            DO $$
            DECLARE def uuid;
            BEGIN
                SELECT "Id" INTO def FROM branches WHERE "IsDefault" = true LIMIT 1;
                IF def IS NULL THEN
                    def := gen_random_uuid();
                    INSERT INTO branches ("Id","Name","IsActive","IsDefault","CreatedAt")
                    VALUES (def, 'Merkez', true, true, now() at time zone 'utc');
                END IF;
                UPDATE cash_accounts SET "BranchId" = def WHERE "BranchId" IS NULL;
                UPDATE orders SET "BranchId" = def WHERE "BranchId" IS NULL;
                UPDATE invoices SET "BranchId" = def WHERE "BranchId" IS NULL;
                UPDATE finance_transactions SET "BranchId" = def WHERE "BranchId" IS NULL;
                UPDATE dining_tables SET "BranchId" = def WHERE "BranchId" IS NULL;
                UPDATE service_areas SET "BranchId" = def WHERE "BranchId" IS NULL;
                UPDATE appointments SET "BranchId" = def WHERE "BranchId" IS NULL;
            END $$;

            CREATE INDEX IF NOT EXISTS "IX_cash_accounts_BranchId" ON cash_accounts ("BranchId");
            CREATE INDEX IF NOT EXISTS "IX_orders_BranchId" ON orders ("BranchId");
            CREATE INDEX IF NOT EXISTS "IX_invoices_BranchId" ON invoices ("BranchId");
            CREATE INDEX IF NOT EXISTS "IX_finance_transactions_BranchId" ON finance_transactions ("BranchId");
            """),

        // Ürün depo yeri: raf + bölge (özellikle tamirci/servis parça takibi için).
        new Migration("0012_product_location",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "ShelfLocation" varchar(60) NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "StorageArea" varchar(60) NULL;
            """),

        // Pazaryeri entegrasyonu (Trendyol): kanal bağlantısı (şifreli kimlik), ürün↔ilan eşleştirme,
        // çekilen pazaryeri siparişleri + faturaya satış kanalı (Channel) kolonu.
        new Migration("0013_marketplace",
            """
            CREATE TABLE IF NOT EXISTS marketplace_connections (
                "Id" uuid PRIMARY KEY,
                "Channel" varchar(40) NOT NULL,
                "SupplierId" varchar(80) NOT NULL,
                "ApiKeyEnc" text NOT NULL,
                "ApiSecretEnc" text NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "LastStockSyncAt" timestamp without time zone NULL,
                "LastOrderSyncAt" timestamp without time zone NULL,
                "LastStatus" varchar(20) NULL,
                "LastMessage" varchar(1000) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_marketplace_connections_Channel"
                ON marketplace_connections ("Channel");

            CREATE TABLE IF NOT EXISTS marketplace_listings (
                "Id" uuid PRIMARY KEY,
                "ConnectionId" uuid NOT NULL REFERENCES marketplace_connections ("Id") ON DELETE CASCADE,
                "ProductId" uuid NOT NULL REFERENCES products ("Id") ON DELETE CASCADE,
                "MarketplaceBarcode" varchar(80) NOT NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "LastPushedStock" numeric(18,4) NULL,
                "LastPushedAt" timestamp without time zone NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_marketplace_listings_Connection_Barcode"
                ON marketplace_listings ("ConnectionId", "MarketplaceBarcode");
            CREATE INDEX IF NOT EXISTS "IX_marketplace_listings_ProductId"
                ON marketplace_listings ("ProductId");

            CREATE TABLE IF NOT EXISTS marketplace_orders (
                "Id" uuid PRIMARY KEY,
                "ConnectionId" uuid NOT NULL REFERENCES marketplace_connections ("Id") ON DELETE CASCADE,
                "Channel" varchar(40) NOT NULL,
                "MarketplaceOrderNumber" varchar(80) NOT NULL,
                "BuyerName" varchar(200) NULL,
                "GrandTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "MarketplaceStatus" varchar(40) NULL,
                "OrderDate" timestamp without time zone NOT NULL,
                "LocalOrderId" uuid NULL,
                "InvoiceId" uuid NULL,
                "SyncStatus" varchar(20) NOT NULL DEFAULT 'Imported',
                "SyncError" varchar(1000) NULL,
                "RawJson" text NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_marketplace_orders_Number"
                ON marketplace_orders ("MarketplaceOrderNumber");
            CREATE INDEX IF NOT EXISTS "IX_marketplace_orders_OrderDate"
                ON marketplace_orders ("OrderDate");

            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "Channel" varchar(40) NOT NULL DEFAULT 'Store';
            """),

        // Sipariş dedupe'i bağlantı (kanal) başına olmalı: farklı pazaryerleri aynı sipariş no'yu kullanabilir.
        // Global tekil index'i düşür, (ConnectionId, MarketplaceOrderNumber) bileşik tekil index kur.
        new Migration("0014_marketplace_order_index_per_connection",
            """
            DROP INDEX IF EXISTS "IX_marketplace_orders_Number";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_marketplace_orders_Conn_Number"
                ON marketplace_orders ("ConnectionId", "MarketplaceOrderNumber");
            """),

        // Tek tuşla Trendyol ilan açma: ürüne marka + çoklu görsel; ilana kategori/marka/kargo/öznitelik/durum meta.
        new Migration("0015_marketplace_listing_creation",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "BrandName" varchar(100) NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "ImageUrls" jsonb NOT NULL DEFAULT '[]'::jsonb;

            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "ListingStatus" varchar(20) NOT NULL DEFAULT 'External';
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "TrendyolCategoryId" integer NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "TrendyolBrandId" integer NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "CargoCompanyId" integer NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "AttributesJson" jsonb NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "BatchRequestId" varchar(80) NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "ListingError" varchar(1000) NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "ListedAt" timestamp without time zone NULL;
            """),

        // Kesin kâr: fatura satırına satış anındaki alış maliyetini sabitle. NULL = feature öncesi/legacy satır
        // (rapor güncel maliyete düşer); yakalanan 0 (gerçek maliyetsiz ürün) NULL'dan ayrışsın diye kolon nullable.
        new Migration("0016_invoice_line_unit_cost",
            """
            ALTER TABLE invoice_lines ADD COLUMN IF NOT EXISTS "UnitCost" numeric(18,4) NULL;
            """),

        // Sadakat/puan: cari puan bakiyesi + faturada kullanılan indirim (₺) + kazanılan puan + ayar config'i.
        new Migration("0017_loyalty_points",
            """
            ALTER TABLE contacts ADD COLUMN IF NOT EXISTS "PointsBalance" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "Discount" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "PointsEarned" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "LoyaltyEnabled" boolean NOT NULL DEFAULT false;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "LoyaltyEarnPercent" numeric(18,4) NOT NULL DEFAULT 0;
            """),

        // Kısmi iade: fatura satırından iade edilen miktar (kalan iade edilebilir = Quantity − RefundedQuantity).
        new Migration("0018_invoice_line_refunded_quantity",
            """
            ALTER TABLE invoice_lines ADD COLUMN IF NOT EXISTS "RefundedQuantity" numeric(18,4) NOT NULL DEFAULT 0;
            """),

        // Çevrimdışı POS idempotency: istemci-üretimi benzersiz satış kimliği; aynı kimlikle ikinci fatura kesilmez
        // (kısmi unique index — NULL'lar hariç, çevrimiçi satışlar etkilenmez).
        new Migration("0019_invoice_client_sale_id",
            """
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "ClientSaleId" varchar(64);
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_invoices_ClientSaleId" ON invoices ("ClientSaleId") WHERE "ClientSaleId" IS NOT NULL;
            """),

        // Randevuya personel atama: master DB User.Id (çapraz-şema, gerçek FK yok). Atanmamışsa NULL.
        new Migration("0020_appointment_staff",
            """
            ALTER TABLE appointments ADD COLUMN IF NOT EXISTS "StaffId" uuid;
            """),

        // Pazaryeri NET kâr: satıcının girdiği komisyon oranı (%) + sipariş başına kargo maliyeti.
        new Migration("0021_marketplace_commission_shipping",
            """
            ALTER TABLE marketplace_connections ADD COLUMN IF NOT EXISTS "CommissionRate" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE marketplace_connections ADD COLUMN IF NOT EXISTS "ShippingCost" numeric(18,4) NOT NULL DEFAULT 0;
            """),

        // İşletme-içi denetim izi: kritik para/stok eylemlerini KİMİN yaptığı (salt-ekle).
        new Migration("0022_audit_events",
            """
            CREATE TABLE IF NOT EXISTS audit_events (
                "Id" uuid PRIMARY KEY,
                "ActorUserId" uuid NULL,
                "ActorEmail" varchar(256) NULL,
                "Action" varchar(60) NOT NULL,
                "TargetType" varchar(40) NULL,
                "TargetId" uuid NULL,
                "Details" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_audit_events_CreatedAt" ON audit_events ("CreatedAt");
            """),

        // Denetim izine şube: kısıtlı yönetici başka şubenin finansal izini görmesin (query filter kullanır).
        new Migration("0023_audit_event_branch",
            """
            ALTER TABLE audit_events ADD COLUMN IF NOT EXISTS "BranchId" uuid;
            CREATE INDEX IF NOT EXISTS "IX_audit_events_BranchId" ON audit_events ("BranchId");
            """),

        // Kalıcı stok sayım oturumu: taslak sunucuda tutulur (cihaz-arası devam) + uygulanınca geçmiş.
        new Migration("0024_stock_count_sessions",
            """
            CREATE TABLE IF NOT EXISTS stock_count_sessions (
                "Id" uuid PRIMARY KEY,
                "Status" varchar(20) NOT NULL,
                "BranchId" uuid NULL,
                "CreatedByUserId" uuid NULL,
                "CreatedByName" varchar(256) NULL,
                "AppliedAt" timestamp without time zone NULL,
                "CountedCount" integer NOT NULL DEFAULT 0,
                "AdjustedCount" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_stock_count_sessions_Status" ON stock_count_sessions ("Status");
            CREATE INDEX IF NOT EXISTS "IX_stock_count_sessions_CreatedAt" ON stock_count_sessions ("CreatedAt");

            CREATE TABLE IF NOT EXISTS stock_count_session_items (
                "Id" uuid PRIMARY KEY,
                "SessionId" uuid NOT NULL REFERENCES stock_count_sessions ("Id") ON DELETE CASCADE,
                "ProductId" uuid NOT NULL,
                "CountedQuantity" numeric(18,4) NOT NULL DEFAULT 0,
                "SystemQuantitySnapshot" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_stock_count_session_items_SessionId" ON stock_count_session_items ("SessionId");
            """),

        // Çok-şube TAM stok: şube başına ürün bakiyesi + hareketlere şube. Product.CurrentStock TOPLAM olarak korunur.
        // Backfill: mevcut tüm stok varsayılan (Merkez) şubeye atanır; şubesiz hareketler de Merkez sayılır.
        new Migration("0025_product_branch_stock",
            """
            ALTER TABLE stock_movements ADD COLUMN IF NOT EXISTS "BranchId" uuid;

            CREATE TABLE IF NOT EXISTS product_branch_stocks (
                "Id" uuid PRIMARY KEY,
                "ProductId" uuid NOT NULL REFERENCES products ("Id") ON DELETE CASCADE,
                "BranchId" uuid NOT NULL,
                "Quantity" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_product_branch_stocks_ProductId_BranchId"
                ON product_branch_stocks ("ProductId", "BranchId");

            -- Mevcut stoğu varsayılan şubeye taşı (yoksa herhangi bir şube). Yalnız stoğu olan ürünler için satır aç.
            INSERT INTO product_branch_stocks ("Id", "ProductId", "BranchId", "Quantity", "CreatedAt")
            SELECT gen_random_uuid(), p."Id", b."Id", p."CurrentStock", now()
            FROM products p
            CROSS JOIN LATERAL (
                SELECT "Id" FROM branches ORDER BY "IsDefault" DESC, "CreatedAt" ASC LIMIT 1
            ) b
            WHERE p."CurrentStock" <> 0
              AND NOT EXISTS (SELECT 1 FROM product_branch_stocks x WHERE x."ProductId" = p."Id");

            -- Eski (şubesiz) stok hareketlerini varsayılan şubeye etiketle.
            UPDATE stock_movements sm
            SET "BranchId" = (SELECT "Id" FROM branches ORDER BY "IsDefault" DESC, "CreatedAt" ASC LIMIT 1)
            WHERE sm."BranchId" IS NULL;
            """),

        new Migration("0026_product_dimensional_weight",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "DimensionalWeight" numeric(18,4) NULL;
            """),

        new Migration("0027_listing_stock_batch",
            """
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "StockBatchRequestId" text NULL;
            ALTER TABLE marketplace_listings ADD COLUMN IF NOT EXISTS "PendingPushStock" numeric(18,4) NULL;
            """),

        new Migration("0028_product_variants",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "ParentProductId" uuid NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "IsVariantParent" boolean NOT NULL DEFAULT false;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "VariantValues" varchar(200) NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "VariantAttributesJson" text NULL;
            CREATE INDEX IF NOT EXISTS "IX_products_ParentProductId" ON products ("ParentProductId");
            """),

        new Migration("0029_recurring_expenses",
            """
            CREATE TABLE IF NOT EXISTS recurring_expenses (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(120) NOT NULL,
                "Amount" numeric(18,4) NOT NULL DEFAULT 0,
                "Category" varchar(120) NULL,
                "CashAccountId" uuid NOT NULL REFERENCES cash_accounts ("Id"),
                "DueDay" integer NOT NULL DEFAULT 1,
                "Description" varchar(500) NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "LastPostedPeriod" varchar(7) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            """),

        new Migration("0030_cash_shifts",
            """
            CREATE TABLE IF NOT EXISTS cash_shifts (
                "Id" uuid PRIMARY KEY,
                "CashAccountId" uuid NOT NULL REFERENCES cash_accounts ("Id"),
                "Status" varchar(10) NOT NULL DEFAULT 'Open',
                "OpenedAt" timestamp without time zone NOT NULL,
                "OpenedByUserId" uuid NULL,
                "OpenedByName" varchar(160) NULL,
                "OpeningFloat" numeric(18,4) NOT NULL DEFAULT 0,
                "ClosedAt" timestamp without time zone NULL,
                "ClosedByUserId" uuid NULL,
                "ClosedByName" varchar(160) NULL,
                "CountedAmount" numeric(18,4) NULL,
                "ExpectedAmount" numeric(18,4) NULL,
                "Difference" numeric(18,4) NULL,
                "BranchId" uuid NULL,
                "Note" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_cash_shifts_CashAccountId_Status" ON cash_shifts ("CashAccountId", "Status");
            """),

        new Migration("0031_cash_shift_single_open",
            """
            -- Bir kasada aynı anda EN FAZLA tek açık vardiya (check-then-insert yarışına karşı DB güvencesi).
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_cash_shifts_one_open_per_account"
                ON cash_shifts ("CashAccountId") WHERE "Status" = 'Open';
            """),

        new Migration("0032_stock_waste_reason",
            """
            ALTER TABLE stock_movements ADD COLUMN IF NOT EXISTS "WasteReason" integer NULL;
            """),

        // Elle indirim: varsayılan KAPALI — para kaybettiren bir yetki, mevcut tenant'larda kendiliğinden açılmamalı.
        new Migration("0033_manual_discount",
            """
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ManualDiscountEnabled" boolean NOT NULL DEFAULT false;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "MaxManualDiscountPercent" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "ManualDiscount" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "DiscountReason" varchar(200) NULL;
            """),

        // Randevu → cari bağı: tahsilat faturası cariye kesilir (ekstre + bakiye + indirim + puan).
        // Cari silme soft-delete (IsActive=false) olduğu için FK asla ihlal edilmez.
        new Migration("0034_appointment_contact",
            """
            ALTER TABLE appointments ADD COLUMN IF NOT EXISTS "ContactId" uuid NULL REFERENCES contacts ("Id");
            CREATE INDEX IF NOT EXISTS "IX_appointments_ContactId" ON appointments ("ContactId");
            """),

        // Terazi barkodu (market): etikete gömülü ürün kodu + ağırlık/fiyat.
        // Varsayılan ön ekler 28,29 — "2"/"20" dahili barkod üretimine ayrılmıştır (çakışma önlendi).
        new Migration("0035_scale_barcode",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "ScaleItemCode" varchar(8) NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_products_ScaleItemCode" ON products ("ScaleItemCode") WHERE "ScaleItemCode" IS NOT NULL;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodeEnabled" boolean NOT NULL DEFAULT false;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodePrefixes" varchar(40) NOT NULL DEFAULT '28,29';
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodeItemDigits" integer NOT NULL DEFAULT 5;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodeValueDigits" integer NOT NULL DEFAULT 5;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodeDecimals" integer NOT NULL DEFAULT 3;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodeEmbeds" integer NOT NULL DEFAULT 0;
            ALTER TABLE settings ADD COLUMN IF NOT EXISTS "ScaleBarcodePriceIncludesVat" boolean NOT NULL DEFAULT true;
            """),

        // Teklif / proforma — BAĞLAYICI DEĞİL: stok/cari/kasa hareketi yok. Kabul edilirse faturaya dönüşür.
        new Migration("0036_quotes",
            """
            CREATE TABLE IF NOT EXISTS quotes (
                "Id" uuid PRIMARY KEY,
                "Number" varchar(40) NOT NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Draft',
                "ContactId" uuid NULL REFERENCES contacts ("Id") ON DELETE SET NULL,
                "CustomerName" varchar(160) NULL,
                "Date" timestamp without time zone NOT NULL,
                "ValidUntil" timestamp without time zone NOT NULL,
                "Subtotal" numeric(18,4) NOT NULL DEFAULT 0,
                "VatTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "GrandTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "Note" varchar(2000) NULL,
                "BranchId" uuid NULL,
                "InvoiceId" uuid NULL,
                "DecidedAt" timestamp without time zone NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE TABLE IF NOT EXISTS quote_lines (
                "Id" uuid PRIMARY KEY,
                "QuoteId" uuid NOT NULL REFERENCES quotes ("Id") ON DELETE CASCADE,
                "ProductId" uuid NOT NULL REFERENCES products ("Id"),
                "ProductName" varchar(200) NOT NULL,
                "Quantity" numeric(18,4) NOT NULL DEFAULT 0,
                "UnitPrice" numeric(18,4) NOT NULL DEFAULT 0,
                "VatRate" numeric(18,4) NOT NULL DEFAULT 0,
                "LineTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "VatAmount" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_quotes_Number" ON quotes ("Number");
            CREATE INDEX IF NOT EXISTS "IX_quotes_Date" ON quotes ("Date");
            CREATE INDEX IF NOT EXISTS "IX_quotes_BranchId" ON quotes ("BranchId");
            CREATE INDEX IF NOT EXISTS "IX_quotes_Status" ON quotes ("Status");
            CREATE INDEX IF NOT EXISTS "IX_quote_lines_QuoteId" ON quote_lines ("QuoteId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_quotes_InvoiceId" ON quotes ("InvoiceId") WHERE "InvoiceId" IS NOT NULL;
            """),

        // Tedarikçi siparişi + mal kabul. Ayrı irsaliye tablosu YOK: mal kabul = siparişe bağlı ALIŞ faturası
        // (invoices."PurchaseOrderId") → stok/cari/kasa zinciri tek yerde kalır, void geri-alması bedava gelir.
        new Migration("0037_purchase_orders",
            """
            CREATE TABLE IF NOT EXISTS purchase_orders (
                "Id" uuid PRIMARY KEY,
                "Number" varchar(40) NOT NULL,
                "ContactId" uuid NOT NULL REFERENCES contacts ("Id"),
                "Status" varchar(20) NOT NULL DEFAULT 'Draft',
                "OrderDate" timestamp without time zone NOT NULL,
                "ExpectedDate" timestamp without time zone NULL,
                "BranchId" uuid NULL,
                "Note" varchar(1000) NULL,
                "Subtotal" numeric(18,4) NOT NULL DEFAULT 0,
                "VatTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "GrandTotal" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedByUserId" uuid NULL,
                "CreatedByName" varchar(256) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE TABLE IF NOT EXISTS purchase_order_lines (
                "Id" uuid PRIMARY KEY,
                "PurchaseOrderId" uuid NOT NULL REFERENCES purchase_orders ("Id") ON DELETE CASCADE,
                "ProductId" uuid NOT NULL REFERENCES products ("Id"),
                "ProductName" varchar(200) NOT NULL,
                "OrderedQuantity" numeric(18,4) NOT NULL DEFAULT 0,
                "ReceivedQuantity" numeric(18,4) NOT NULL DEFAULT 0,
                "UnitPrice" numeric(18,4) NOT NULL DEFAULT 0,
                "VatRate" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_purchase_orders_Number" ON purchase_orders ("Number");
            CREATE INDEX IF NOT EXISTS "IX_purchase_orders_Status" ON purchase_orders ("Status");
            CREATE INDEX IF NOT EXISTS "IX_purchase_orders_BranchId" ON purchase_orders ("BranchId");
            CREATE INDEX IF NOT EXISTS "IX_purchase_order_lines_PurchaseOrderId" ON purchase_order_lines ("PurchaseOrderId");
            CREATE INDEX IF NOT EXISTS "IX_purchase_order_lines_ProductId" ON purchase_order_lines ("ProductId");
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "PurchaseOrderId" uuid NULL;
            CREATE INDEX IF NOT EXISTS "IX_invoices_PurchaseOrderId" ON invoices ("PurchaseOrderId");
            """),

        // AI Asistan öğrenme döngüsü ("sora sora eğit"): anlaşılamayan sorular + admin etiketiyle büyüyen
        // eğitim seti. Motor gömülü seed'e ek olarak assistant_training satırlarını yükler.
        new Migration("0038_assistant_learning",
            """
            CREATE TABLE IF NOT EXISTS assistant_training (
                "Id" uuid PRIMARY KEY,
                "Intent" varchar(40) NOT NULL,
                "Text" varchar(500) NOT NULL,
                "Source" varchar(20) NOT NULL DEFAULT 'learned',
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE TABLE IF NOT EXISTS assistant_unresolved (
                "Id" uuid PRIMARY KEY,
                "Question" varchar(500) NOT NULL,
                "QuestionKey" varchar(500) NOT NULL DEFAULT '',
                "Count" integer NOT NULL DEFAULT 1,
                "IsResolved" boolean NOT NULL DEFAULT false,
                "ResolvedIntent" varchar(40) NULL,
                "AskedByEmail" varchar(256) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            -- Erken kurulmuş tenant'lar için kendini onar (kolon sonradan eklendi).
            ALTER TABLE assistant_unresolved ADD COLUMN IF NOT EXISTS "QuestionKey" varchar(500) NOT NULL DEFAULT '';
            CREATE INDEX IF NOT EXISTS "IX_assistant_training_Intent" ON assistant_training ("Intent");
            CREATE INDEX IF NOT EXISTS "IX_assistant_unresolved_IsResolved" ON assistant_unresolved ("IsResolved");
            -- Çözülmemişler arasında normalize soru tekildir → dedup + eşzamanlı çift-ekleme koruması.
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_assistant_unresolved_QuestionKey"
                ON assistant_unresolved ("QuestionKey") WHERE "IsResolved" = false;
            """),

        // Kalıcı bildirim merkezi (zil): düşük stok, geciken alacak, yaklaşan randevu, kasa farkı, anomali vb.
        // Zil "kaydet-oku" modeline geçer; DedupKey ile aynı okunmamış olay tekilleşir (tarama çoğaltmaz).
        new Migration("0039_notifications",
            """
            CREATE TABLE IF NOT EXISTS notifications (
                "Id" uuid PRIMARY KEY,
                "Type" varchar(40) NOT NULL,
                "Severity" varchar(20) NOT NULL DEFAULT 'info',
                "Title" varchar(200) NOT NULL,
                "Message" varchar(1000) NOT NULL,
                "Link" varchar(300) NULL,
                "Icon" varchar(40) NULL,
                "BranchId" uuid NULL,
                "DedupKey" varchar(200) NULL,
                "IsRead" boolean NOT NULL DEFAULT false,
                "ReadAt" timestamp without time zone NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_notifications_IsRead" ON notifications ("IsRead");
            CREATE INDEX IF NOT EXISTS "IX_notifications_CreatedAt" ON notifications ("CreatedAt");
            CREATE INDEX IF NOT EXISTS "IX_notifications_BranchId" ON notifications ("BranchId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_notifications_DedupKey"
                ON notifications ("DedupKey") WHERE "DedupKey" IS NOT NULL AND "IsRead" = false;
            """),

        // Vade yönetimi: fatura + cari hareketinde ödeme vadesi. Yaşlandırma artık DueDate'e göre; nakit akışı
        // tahmini ve tahsilat hatırlatması (dunning) bu tarihi kullanır. null = peşin/vadesiz (geriye uyumlu).
        new Migration("0040_due_date",
            """
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "DueDate" timestamp without time zone NULL;
            ALTER TABLE account_transactions ADD COLUMN IF NOT EXISTS "DueDate" timestamp without time zone NULL;
            CREATE INDEX IF NOT EXISTS "IX_account_transactions_DueDate" ON account_transactions ("DueDate");
            CREATE INDEX IF NOT EXISTS "IX_invoices_DueDate" ON invoices ("DueDate");
            """),

        // Reçete / ürün ağacı (BOM): bileşik ürün (ör. Latte) satılınca bileşenleri (çekirdek/süt/bardak) stoktan düşer.
        new Migration("0041_recipe_components",
            """
            CREATE TABLE IF NOT EXISTS recipe_components (
                "Id" uuid PRIMARY KEY,
                "ProductId" uuid NOT NULL REFERENCES products ("Id") ON DELETE CASCADE,
                "ComponentProductId" uuid NOT NULL REFERENCES products ("Id"),
                "Quantity" numeric(18,4) NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_recipe_components_ProductId" ON recipe_components ("ProductId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_recipe_components_Product_Component"
                ON recipe_components ("ProductId", "ComponentProductId");
            """),

        // Çek/senet portföyü (Türkiye B2B): alınan/verilen kıymetli evrak — vade + durum (portföy/tahsil/ciro/karşılıksız).
        new Migration("0042_cheques",
            """
            CREATE TABLE IF NOT EXISTS cheques (
                "Id" uuid PRIMARY KEY,
                "Kind" varchar(20) NOT NULL,
                "Direction" varchar(20) NOT NULL,
                "ContactId" uuid NULL,
                "ContactName" varchar(200) NULL,
                "Amount" numeric(18,4) NOT NULL DEFAULT 0,
                "DueDate" timestamp without time zone NOT NULL,
                "Bank" varchar(120) NULL,
                "SerialNo" varchar(80) NULL,
                "Status" varchar(20) NOT NULL DEFAULT 'Portfolio',
                "Note" varchar(500) NULL,
                "BranchId" uuid NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_cheques_DueDate" ON cheques ("DueDate");
            CREATE INDEX IF NOT EXISTS "IX_cheques_Status" ON cheques ("Status");
            CREATE INDEX IF NOT EXISTS "IX_cheques_ContactId" ON cheques ("ContactId");
            """),

        // Müşteri notları/segment + doğum günü + risk limiti (CRM derinleştirme).
        new Migration("0043_contact_crm_fields",
            """
            ALTER TABLE contacts ADD COLUMN IF NOT EXISTS "Notes" varchar(1000) NULL;
            ALTER TABLE contacts ADD COLUMN IF NOT EXISTS "Tags" varchar(300) NULL;
            ALTER TABLE contacts ADD COLUMN IF NOT EXISTS "Birthday" timestamp without time zone NULL;
            ALTER TABLE contacts ADD COLUMN IF NOT EXISTS "CreditLimit" numeric(18,4) NULL;
            """),

        // Ürün son kullanma tarihi (SKT) — yaklaşan/geçen SKT bildirimi (market/kafe fire önleme).
        new Migration("0044_product_expiry",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "ExpiryDate" timestamp without time zone NULL;
            CREATE INDEX IF NOT EXISTS "IX_products_ExpiryDate" ON products ("ExpiryDate");
            """),

        // Ürün-tedarikçi eşlemesi (#37): tercih tedarikçi, tedarikçi SKU, son alış, teslim süresi, min sipariş.
        new Migration("0045_product_suppliers",
            """
            CREATE TABLE IF NOT EXISTS product_suppliers (
                "Id" uuid PRIMARY KEY,
                "ProductId" uuid NOT NULL REFERENCES products ("Id") ON DELETE CASCADE,
                "ContactId" uuid NOT NULL REFERENCES contacts ("Id"),
                "SupplierSku" varchar(80) NULL,
                "LastPurchasePrice" numeric(18,4) NOT NULL DEFAULT 0,
                "LeadTimeDays" integer NOT NULL DEFAULT 0,
                "MinOrderQuantity" numeric(18,4) NOT NULL DEFAULT 0,
                "IsPreferred" boolean NOT NULL DEFAULT false,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_product_suppliers_ProductId" ON product_suppliers ("ProductId");
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_product_suppliers_Product_Contact"
                ON product_suppliers ("ProductId", "ContactId");
            """),

        // Genel dosya/ek altyapısı (#23): herhangi bir kayda (cari/fatura/ürün/sipariş/garanti) dosya iliştirme.
        new Migration("0046_attachments",
            """
            CREATE TABLE IF NOT EXISTS attachments (
                "Id" uuid PRIMARY KEY,
                "OwnerType" varchar(20) NOT NULL,
                "OwnerId" uuid NOT NULL,
                "Url" varchar(500) NOT NULL,
                "FileName" varchar(260) NOT NULL,
                "ContentType" varchar(120) NULL,
                "Note" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_attachments_OwnerType_OwnerId" ON attachments ("OwnerType", "OwnerId");
            """),

        // Servis/garanti kaydı (#49): satılan ürünün garanti takibi (seri no, alış tarihi, garanti süresi).
        new Migration("0047_warranty_records",
            """
            CREATE TABLE IF NOT EXISTS warranty_records (
                "Id" uuid PRIMARY KEY,
                "CustomerName" varchar(200) NOT NULL,
                "ContactId" uuid NULL REFERENCES contacts ("Id") ON DELETE SET NULL,
                "ProductName" varchar(200) NOT NULL,
                "SerialNo" varchar(80) NULL,
                "PurchaseDate" timestamp without time zone NOT NULL,
                "WarrantyMonths" integer NOT NULL DEFAULT 0,
                "Note" varchar(500) NULL,
                "BranchId" uuid NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_warranty_records_PurchaseDate" ON warranty_records ("PurchaseDate");
            CREATE INDEX IF NOT EXISTS "IX_warranty_records_ContactId" ON warranty_records ("ContactId");
            CREATE INDEX IF NOT EXISTS "IX_warranty_records_BranchId" ON warranty_records ("BranchId");
            """),

        // Referans/tavsiye programı (#50): tavsiye eden cari için benzersiz kod, ödül + durum.
        new Migration("0048_referrals",
            """
            CREATE TABLE IF NOT EXISTS referrals (
                "Id" uuid PRIMARY KEY,
                "ReferrerContactId" uuid NOT NULL,
                "Code" varchar(20) NOT NULL,
                "ReferredContactId" uuid NULL,
                "RewardAmount" numeric(18,4) NOT NULL DEFAULT 0,
                "Status" varchar(20) NOT NULL DEFAULT 'Pending',
                "Note" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_referrals_Code" ON referrals ("Code");
            CREATE INDEX IF NOT EXISTS "IX_referrals_ReferrerContactId" ON referrals ("ReferrerContactId");
            """),

        // Hediye çeki (#35): kesim, bakiye, harcama (redeem), iptal.
        new Migration("0049_gift_cards",
            """
            CREATE TABLE IF NOT EXISTS gift_cards (
                "Id" uuid PRIMARY KEY,
                "Code" varchar(40) NOT NULL,
                "InitialBalance" numeric(18,4) NOT NULL DEFAULT 0,
                "Balance" numeric(18,4) NOT NULL DEFAULT 0,
                "Status" varchar(20) NOT NULL DEFAULT 'Active',
                "ContactId" uuid NULL,
                "ExpiresAt" timestamp without time zone NULL,
                "Note" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS "UX_gift_cards_Code" ON gift_cards ("Code");
            CREATE INDEX IF NOT EXISTS "IX_gift_cards_ContactId" ON gift_cards ("ContactId");
            """),

        // Kural motoru (#41): if-this-then-that otomasyon tanımları (tetikleyici+eylem; çalıştırma motoru yok).
        new Migration("0050_automation_rules",
            """
            CREATE TABLE IF NOT EXISTS automation_rules (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(120) NOT NULL,
                "TriggerType" varchar(40) NOT NULL,
                "ConditionJson" jsonb NULL,
                "ActionType" varchar(20) NOT NULL,
                "ActionConfigJson" jsonb NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_automation_rules_TriggerType" ON automation_rules ("TriggerType");
            CREATE INDEX IF NOT EXISTS "IX_automation_rules_IsActive" ON automation_rules ("IsActive");
            """),

        // Satan personel (#20): fatura → personel bağı (personel bazlı satış/performans raporu + prim).
        new Migration("0051_invoice_seller",
            """
            ALTER TABLE invoices ADD COLUMN IF NOT EXISTS "SellerUserId" uuid NULL;
            CREATE INDEX IF NOT EXISTS "IX_invoices_SellerUserId" ON invoices ("SellerUserId");
            """),

        // Prim/komisyon kuralları (#19): personele özel veya genel yüzde oran; dönemsel prim hesabı bunu kullanır.
        new Migration("0052_commission_rules",
            """
            CREATE TABLE IF NOT EXISTS commission_rules (
                "Id" uuid PRIMARY KEY,
                "StaffUserId" uuid NULL,
                "Rate" numeric(18,4) NOT NULL DEFAULT 0,
                "IsActive" boolean NOT NULL DEFAULT true,
                "Note" varchar(500) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_commission_rules_StaffUserId" ON commission_rules ("StaffUserId");
            CREATE INDEX IF NOT EXISTS "IX_commission_rules_IsActive" ON commission_rules ("IsActive");
            """),

        // Kampanya/promosyon (#16): kategori/ürün %, happy hour, X al Y öde. Fiyat yolu değişmez; Evaluate ile hesaplanır.
        new Migration("0053_campaigns",
            """
            CREATE TABLE IF NOT EXISTS campaigns (
                "Id" uuid PRIMARY KEY,
                "Name" varchar(160) NOT NULL,
                "Type" varchar(30) NOT NULL,
                "CategoryId" uuid NULL,
                "ProductId" uuid NULL,
                "Percent" numeric(18,4) NULL,
                "BuyQty" integer NULL,
                "GetQty" integer NULL,
                "StartDate" timestamp without time zone NULL,
                "EndDate" timestamp without time zone NULL,
                "StartHour" integer NULL,
                "EndHour" integer NULL,
                "DaysMask" varchar(30) NULL,
                "IsActive" boolean NOT NULL DEFAULT true,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_campaigns_IsActive" ON campaigns ("IsActive");
            CREATE INDEX IF NOT EXISTS "IX_campaigns_Type" ON campaigns ("Type");
            """),

        // Çoklu ölçü birimi (#26): koli↔adet — alış birimi + katsayı (satış birimi Product.Unit).
        new Migration("0054_product_purchase_unit",
            """
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "PurchaseUnit" varchar(20) NULL;
            ALTER TABLE products ADD COLUMN IF NOT EXISTS "PurchaseUnitFactor" numeric(18,4) NULL;
            """),

        // Ürün opsiyonları (#29): az şekerli / ekstra shot / boy — grup + ad + fiyat farkı. POS satırda uygular.
        new Migration("0055_product_options",
            """
            CREATE TABLE IF NOT EXISTS product_options (
                "Id" uuid PRIMARY KEY,
                "ProductId" uuid NOT NULL REFERENCES products ("Id") ON DELETE CASCADE,
                "GroupName" varchar(80) NOT NULL,
                "Name" varchar(120) NOT NULL,
                "PriceDelta" numeric(18,4) NOT NULL DEFAULT 0,
                "SortOrder" integer NOT NULL DEFAULT 0,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL);
            CREATE INDEX IF NOT EXISTS "IX_product_options_ProductId" ON product_options ("ProductId");
            """),

        // Pazaryeri komisyon-muhasebe (#39): fee alanlarını mevcut tenant'larda garanti et (idempotent — zaten varsa no-op).
        new Migration("0056_marketplace_fees",
            """
            ALTER TABLE marketplace_connections ADD COLUMN IF NOT EXISTS "CommissionRate" numeric(18,4) NOT NULL DEFAULT 0;
            ALTER TABLE marketplace_connections ADD COLUMN IF NOT EXISTS "ShippingCost" numeric(18,4) NOT NULL DEFAULT 0;
            """),

        // #6 Web Push abonelikleri — tarayıcı push hedefleri (bildirim üretilince gönderilir).
        new Migration("0057_push_subscriptions",
            """
            CREATE TABLE IF NOT EXISTS push_subscriptions (
                "Id" uuid PRIMARY KEY,
                "UserId" uuid NOT NULL,
                "Endpoint" varchar(600) NOT NULL,
                "P256dh" varchar(200) NOT NULL,
                "Auth" varchar(200) NOT NULL,
                "UserAgent" varchar(300) NULL,
                "CreatedAt" timestamp without time zone NOT NULL,
                "UpdatedAt" timestamp without time zone NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_push_subscriptions_Endpoint" ON push_subscriptions ("Endpoint");
            CREATE INDEX IF NOT EXISTS "IX_push_subscriptions_UserId" ON push_subscriptions ("UserId");
            """),
    ];
}
