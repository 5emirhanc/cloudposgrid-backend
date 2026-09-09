using FluentValidation;

namespace CloudPosGrid.Application.Modules.Stock;

public class CreateCategoryRequestValidator : AbstractValidator<CreateCategoryRequest>
{
    public CreateCategoryRequestValidator() =>
        RuleFor(x => x.Name).NotEmpty().WithMessage("Kategori adı zorunlu.").MaximumLength(150);
}

public class UpdateCategoryRequestValidator : AbstractValidator<UpdateCategoryRequest>
{
    public UpdateCategoryRequestValidator() =>
        RuleFor(x => x.Name).NotEmpty().WithMessage("Kategori adı zorunlu.").MaximumLength(150);
}

public class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Ürün adı zorunlu.").MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000).WithMessage("Açıklama en fazla 1000 karakter olabilir.");
        RuleFor(x => x.Sku).MaximumLength(64);
        RuleFor(x => x.Barcode).MaximumLength(64);
        RuleFor(x => x.PurchasePrice).GreaterThanOrEqualTo(0).WithMessage("Alış fiyatı negatif olamaz.");
        RuleFor(x => x.SalePrice).GreaterThanOrEqualTo(0).WithMessage("Satış fiyatı negatif olamaz.");
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100).WithMessage("KDV oranı 0-100 arası olmalı.");
        RuleFor(x => x.OpeningStock).GreaterThanOrEqualTo(0);
        RuleFor(x => x.MinStock).GreaterThanOrEqualTo(0);
    }
}

public class UpdateProductRequestValidator : AbstractValidator<UpdateProductRequest>
{
    public UpdateProductRequestValidator()
    {
        RuleFor(x => x.Sku).NotEmpty().WithMessage("Stok kodu (SKU) zorunlu.").MaximumLength(64);
        RuleFor(x => x.Name).NotEmpty().WithMessage("Ürün adı zorunlu.").MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(1000).WithMessage("Açıklama en fazla 1000 karakter olabilir.");
        RuleFor(x => x.Barcode).MaximumLength(64);
        RuleFor(x => x.PurchasePrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.SalePrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.MinStock).GreaterThanOrEqualTo(0);
    }
}

public class CreateStockMovementRequestValidator : AbstractValidator<CreateStockMovementRequest>
{
    public CreateStockMovementRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThanOrEqualTo(0).WithMessage("Miktar negatif olamaz.");
    }
}

public class ApplyStockCountRequestValidator : AbstractValidator<ApplyStockCountRequest>
{
    public ApplyStockCountRequestValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("Sayılacak ürün yok.");
        RuleForEach(x => x.Items).ChildRules(i =>
        {
            i.RuleFor(x => x.ProductId).NotEmpty();
            i.RuleFor(x => x.CountedQuantity).GreaterThanOrEqualTo(0).WithMessage("Sayılan miktar negatif olamaz.");
        });
    }
}
