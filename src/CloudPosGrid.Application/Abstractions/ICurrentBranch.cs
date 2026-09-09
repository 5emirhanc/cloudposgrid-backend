namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Geçerli isteğin şube bağlamı (çok şube). Frontend her isteğe <c>X-Branch-Id</c> başlığı ekler.
/// Okuma sorgularında <see cref="HeaderBranchId"/> ile süzülür; yazmada
/// <see cref="ResolveWriteBranchAsync"/> ile kesin bir şubeye (başlık geçerliyse o, değilse varsayılan) bağlanır.
/// </summary>
public interface ICurrentBranch
{
    /// <summary>İstek başlığındaki şube (geçerli GUID ise); yoksa null → tüm şubeler (birleşik).</summary>
    Guid? HeaderBranchId { get; }

    /// <summary>Yeni kayıtların bağlanacağı kesin şube: başlıktaki geçerliyse o, değilse varsayılan şube.</summary>
    Task<Guid> ResolveWriteBranchAsync(CancellationToken ct = default);
}
