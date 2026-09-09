namespace CloudPosGrid.Application.Common;

/// <summary>İş kuralı/uygulama hatalarının temel tipi; API'de uygun HTTP koduna çevrilir.</summary>
public abstract class AppException : Exception
{
    public abstract int StatusCode { get; }
    /// <summary>İstemcinin ayırt edebilmesi için makine-okunur kod (ör. yükseltme yönlendirmesi).</summary>
    public virtual string? Code => null;
    protected AppException(string message) : base(message) { }
}

/// <summary>403 — Özellik mevcut pakette yok; Kurumsal'a (ya da ücretli plana) yükseltme gerekir.</summary>
public sealed class PlanUpgradeException : AppException
{
    public override int StatusCode => 403;
    public override string? Code => "PLAN_UPGRADE_REQUIRED";
    public PlanUpgradeException(string message) : base(message) { }
}

/// <summary>404 — Kayıt bulunamadı.</summary>
public sealed class NotFoundException : AppException
{
    public override int StatusCode => 404;
    public NotFoundException(string message) : base(message) { }
    public static NotFoundException For(string entity, object id) => new($"{entity} bulunamadı: {id}");
}

/// <summary>409 — Çakışma (ör. e-posta zaten kayıtlı).</summary>
public sealed class ConflictException : AppException
{
    public override int StatusCode => 409;
    public ConflictException(string message) : base(message) { }
}

/// <summary>400 — İş kuralı / doğrulama hatası.</summary>
public sealed class BusinessRuleException : AppException
{
    public override int StatusCode => 400;
    public BusinessRuleException(string message) : base(message) { }
}

/// <summary>401 — Kimlik doğrulama hatası.</summary>
public sealed class UnauthorizedAppException : AppException
{
    public override int StatusCode => 401;
    public UnauthorizedAppException(string message) : base(message) { }
}
