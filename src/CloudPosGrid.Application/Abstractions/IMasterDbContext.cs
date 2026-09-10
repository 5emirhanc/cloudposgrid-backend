using CloudPosGrid.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Application.Abstractions;

/// <summary>public şemadaki kiracı-üstü veriler için DbContext soyutlaması.</summary>
public interface IMasterDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<Dealer> Dealers { get; }
    DbSet<Account> Accounts { get; }
    DbSet<User> Users { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<EmailVerification> EmailVerifications { get; }
    DbSet<SubscriptionRequest> SubscriptionRequests { get; }
    DbSet<TenantPayment> TenantPayments { get; }
    DbSet<DealerPayout> DealerPayouts { get; }
    DbSet<AuditLog> AuditLogs { get; }
    /// <summary>Oturum açma kayıtları (başarılı/başarısız giriş — IP/cihaz) — #46 oturum geçmişi.</summary>
    DbSet<LoginEvent> LoginEvents { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
