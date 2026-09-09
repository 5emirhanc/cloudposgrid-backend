using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Infrastructure.Persistence;

/// <summary>public şemadaki kiracı-üstü veriler: işletmeler, kullanıcılar, refresh token'lar.</summary>
public class MasterDbContext : DbContext, IMasterDbContext
{
    public MasterDbContext(DbContextOptions<MasterDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Dealer> Dealers => Set<Dealer>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<EmailVerification> EmailVerifications => Set<EmailVerification>();
    public DbSet<SubscriptionRequest> SubscriptionRequests => Set<SubscriptionRequest>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<LoginEvent> LoginEvents => Set<LoginEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("public");

        b.Entity<Tenant>(e =>
        {
            e.ToTable("tenants");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Slug).HasMaxLength(120).IsRequired();
            e.Property(x => x.SchemaName).HasMaxLength(80).IsRequired();
            e.Property(x => x.Plan).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BusinessType).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BillingCycle).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.AdminNote).HasMaxLength(500);
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.SchemaName).IsUnique();
            // İyimser eşzamanlılık: SubscriptionEndsAt/TrialEndsAt okuma-değiştirme-yazma (aktivasyon,
            // uzatma, onay) paralel çağrıda birbirini ezmesin → çift/kayıp abonelik süresi. xmin sistem
            // sütunudur, migration gerekmez; çakışma → DbUpdateConcurrencyException → 409.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            // #25 bayi: onboard eden bayiye gevşek bağ; bayi silinince tenant korunur (DealerId → null).
            e.HasOne(x => x.Dealer).WithMany(d => d.Tenants)
                .HasForeignKey(x => x.DealerId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.DealerId);
        });

        b.Entity<Dealer>(e =>
        {
            e.ToTable("dealers");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.Code).HasMaxLength(40).IsRequired();
            e.Property(x => x.CommissionRate).HasPrecision(9, 2);
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<SubscriptionRequest>(e =>
        {
            e.ToTable("subscription_requests");
            e.Property(x => x.RequestedPlan).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.BillingCycle).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.Note).HasMaxLength(500);
            e.Property(x => x.DecidedByEmail).HasMaxLength(256);
            e.HasIndex(x => new { x.TenantId, x.Status });
            // Tenant başına en çok BİR bekleyen (Pending) talep — eşzamanlı çift talep oluşmasını DB
            // düzeyinde engeller (uygulama AnyAsync kontrolü TOCTOU'ya açık). İkinci insert → unique
            // ihlali → ConflictException (SubscriptionService).
            e.HasIndex(x => x.TenantId).IsUnique().HasFilter("\"Status\" = 'Pending'")
                .HasDatabaseName("IX_subscription_requests_TenantId_Pending");
            // İyimser eşzamanlılık: aynı Pending talebi iki admin aynı anda onaylayamasın (çift aktivasyon
            // = bedava abonelik süresi). Çakışma → 409, tüm aktivasyon geri alınır.
            e.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.HasOne(x => x.Tenant).WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.Property(x => x.ActorEmail).HasMaxLength(256).IsRequired();
            e.Property(x => x.Action).HasMaxLength(80).IsRequired();
            e.Property(x => x.TargetType).HasMaxLength(60);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.TenantId);
            // Tenant'a FK YOK bilinçli: işletme silinince audit izi kalmalı (immutable trail).
        });

        b.Entity<LoginEvent>(e =>
        {
            e.ToTable("login_events");
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(400);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.CreatedAt);
            // Tenant/User'a FK YOK bilinçli: hesap/işletme silinse de giriş izi kalır (güvenlik trail).
        });

        b.Entity<Account>(e =>
        {
            e.ToTable("accounts");
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.TwoFactorSecret).HasMaxLength(64);
            // Benzersizlik ARTIK burada (login kimliği). Bir e-posta = bir Account, N işletme üyeliği.
            e.HasIndex(x => x.Email).IsUnique();
        });

        b.Entity<User>(e =>
        {
            e.ToTable("users");
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            // #47: e-posta ARTIK global-unique DEĞİL (benzersizlik Account'ta). Aynı e-posta N tenant'ta üye olabilir.
            e.HasIndex(x => x.Email);
            // Aynı hesap aynı tenant'a iki kez üye olamaz.
            e.HasIndex(x => new { x.AccountId, x.TenantId }).IsUnique();
            e.HasOne(x => x.Tenant).WithMany(t => t.Users)
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Account).WithMany(a => a.Memberships)
                .HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailVerification>(e =>
        {
            e.ToTable("email_verifications");
            e.Ignore(x => x.IsUsable);
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Code).HasMaxLength(10).IsRequired();
            e.HasIndex(x => x.Email);
        });

        b.Entity<RefreshToken>(e =>
        {
            e.ToTable("refresh_tokens");
            e.Ignore(x => x.IsActive);
            e.Property(x => x.TokenHash).IsRequired();
            e.HasIndex(x => x.TokenHash);
            e.HasOne(x => x.User).WithMany(u => u.RefreshTokens)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        return base.SaveChangesAsync(cancellationToken);
    }
}
