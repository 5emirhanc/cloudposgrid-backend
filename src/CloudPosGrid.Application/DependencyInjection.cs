using CloudPosGrid.Application.Modules.Appointments;
using CloudPosGrid.Application.Modules.Assistant;
using CloudPosGrid.Application.Modules.Auth;
using CloudPosGrid.Application.Modules.Contacts;
using CloudPosGrid.Application.Modules.Dashboard;
using CloudPosGrid.Application.Modules.Finance;
using CloudPosGrid.Application.Modules.Invoices;
using CloudPosGrid.Application.Modules.Purchasing;
using CloudPosGrid.Application.Modules.Quotes;
using CloudPosGrid.Application.Modules.Menu;
using CloudPosGrid.Application.Modules.Orders;
using CloudPosGrid.Application.Modules.Settings;
using CloudPosGrid.Application.Modules.Stock;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace CloudPosGrid.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<Modules.Auth.ILoginAuditService, Modules.Auth.LoginAuditService>(); // #46 oturum geçmişi
        services.AddScoped<Modules.Staff.IStaffService, Modules.Staff.StaffService>();

        // Stok modülü
        // İşletme-içi denetim izi (kim neyi değiştirdi) — servislerin SaveChanges'ine iliştirilir.
        services.AddScoped<Abstractions.IAuditTrail, Common.AuditTrail>();
        services.AddScoped<Modules.Audit.IAuditService, Modules.Audit.AuditService>();

        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<IReplenishmentService, ReplenishmentService>();
        services.AddScoped<IProductOptionService, ProductOptionService>(); // ürün opsiyonları (#29)
        services.AddScoped<Modules.Push.IPushService, Modules.Push.PushService>(); // web push (#6)
        services.AddScoped<Modules.Dealers.IDealerService, Modules.Dealers.DealerService>(); // bayi/yeniden-satıcı (#25)
        // Reçete/BOM (bileşik ürün → bileşen stoğu)
        services.AddScoped<Modules.Recipes.IRecipeService, Modules.Recipes.RecipeService>();

        // AI Asistan (kendi niyet motorumuz — harici API yok)
        services.AddScoped<IAssistantService, AssistantService>();
        services.AddScoped<IInsightService, InsightService>();

        // Kalıcı bildirim merkezi (zil) — düşük stok/geciken alacak/anomali vb. için ortak zemin.
        services.AddScoped<Modules.Notifications.INotificationService, Modules.Notifications.NotificationService>();
        // Proaktif tarama (üretici): düşük stok + randevu hatırlatma (SMS) + geciken alacak/dunning (SMS).
        services.AddScoped<Modules.Notifications.INotificationScanService, Modules.Notifications.NotificationScanService>();

        // Ön muhasebe modülü
        services.AddScoped<IContactService, ContactService>();
        services.AddScoped<ICashAccountService, CashAccountService>();
        services.AddScoped<IFinanceService, FinanceService>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<IQuoteService, QuoteService>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        // Öneriden tek-tık sipariş (#36) — akıllı öneri + tercih tedarikçi → taslak PO
        services.AddScoped<Modules.Purchasing.IAutoReorderService, Modules.Purchasing.AutoReorderService>();
        // Kampanya/promosyon motoru (#16) — tanım + değerlendirme (fiyat yolu değişmez)
        services.AddScoped<Modules.Campaigns.ICampaignService, Modules.Campaigns.CampaignService>();
        services.AddScoped<ISettingsService, SettingsService>();
        // Çek/senet portföyü
        services.AddScoped<Modules.Cheques.IChequeService, Modules.Cheques.ChequeService>();
        // Eklemeli özellikler batch'i (workflow): ürün-tedarikçi, dosya/ek, garanti, referans, hediye çeki
        services.AddScoped<Modules.Purchasing.IProductSupplierService, Modules.Purchasing.ProductSupplierService>();
        services.AddScoped<Modules.Files.IAttachmentService, Modules.Files.AttachmentService>();
        services.AddScoped<Modules.Warranties.IWarrantyService, Modules.Warranties.WarrantyService>();
        services.AddScoped<Modules.Referrals.IReferralService, Modules.Referrals.ReferralService>();
        services.AddScoped<Modules.GiftCards.IGiftCardService, Modules.GiftCards.GiftCardService>();
        // Batch-2: kural motoru + analitik (anomali/ABC/nakit akışı) + yedek
        services.AddScoped<Modules.Automation.IAutomationRuleService, Modules.Automation.AutomationRuleService>();
        services.AddScoped<Modules.Automation.IAutomationEngine, Modules.Automation.AutomationEngine>(); // kuralları ÇALIŞTIRAN motor
        services.AddScoped<Modules.Analytics.IAnomalyService, Modules.Analytics.AnomalyService>();
        services.AddScoped<Modules.Analytics.IInventoryAnalyticsService, Modules.Analytics.InventoryAnalyticsService>();
        services.AddScoped<Modules.Analytics.ICashflowForecastService, Modules.Analytics.CashflowForecastService>();
        services.AddScoped<Modules.Backup.IBackupService, Modules.Backup.BackupService>();
        // Batch-3: çok-şube konsolide panel + prim/komisyon + zamanlı rapor içeriği
        services.AddScoped<Modules.Analytics.IBranchAnalyticsService, Modules.Analytics.BranchAnalyticsService>();
        services.AddScoped<Modules.Commissions.ICommissionService, Modules.Commissions.CommissionService>();
        services.AddScoped<Modules.Reporting.IScheduledReportService, Modules.Reporting.ScheduledReportService>();

        // Çok şube (hafif)
        services.AddScoped<Modules.Branches.IBranchService, Modules.Branches.BranchService>();

        // Adisyon modülü (hospitality)
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IDiningService, DiningService>();

        // QR menü (public)
        services.AddScoped<IMenuService, MenuService>();

        // Randevu (güzellik/kuaför)
        services.AddScoped<IAppointmentService, AppointmentService>();

        // Dashboard / raporlar
        services.AddScoped<IDashboardService, DashboardService>();
        services.AddScoped<Modules.Reports.IReportService, Modules.Reports.ReportService>();

        // Platform yönetimi + abonelik (paket/havale)
        services.AddScoped<Modules.Admin.IAdminService, Modules.Admin.AdminService>();
        services.AddScoped<Modules.Subscription.ISubscriptionService, Modules.Subscription.SubscriptionService>();

        // Pazaryeri entegrasyonu (Trendyol)
        services.AddScoped<Modules.Marketplace.IMarketplaceConnectionService, Modules.Marketplace.MarketplaceConnectionService>();
        services.AddScoped<Modules.Marketplace.IMarketplaceListingService, Modules.Marketplace.MarketplaceListingService>();
        services.AddScoped<Modules.Marketplace.IMarketplaceOrderService, Modules.Marketplace.MarketplaceOrderService>();
        services.AddScoped<Modules.Marketplace.IMarketplaceSyncService, Modules.Marketplace.MarketplaceSyncService>();
        services.AddScoped<Modules.Marketplace.IMarketplaceCommissionService, Modules.Marketplace.MarketplaceCommissionService>(); // #39 komisyon-muhasebe

        return services;
    }
}
