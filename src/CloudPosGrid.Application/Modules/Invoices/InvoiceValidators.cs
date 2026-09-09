using FluentValidation;

namespace CloudPosGrid.Application.Modules.Invoices;

public class CreateInvoiceLineRequestValidator : AbstractValidator<CreateInvoiceLineRequest>
{
    public CreateInvoiceLineRequestValidator()
    {
        RuleFor(x => x.ProductId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Miktar 0'dan büyük olmalı.");
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        RuleFor(x => x.VatRate).InclusiveBetween(0, 100);
    }
}

public class InvoicePaymentRequestValidator : AbstractValidator<InvoicePaymentRequest>
{
    public InvoicePaymentRequestValidator()
    {
        RuleFor(x => x.CashAccountId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0);
    }
}

public class RefundLineRequestValidator : AbstractValidator<RefundLineRequest>
{
    public RefundLineRequestValidator()
    {
        RuleFor(x => x.InvoiceLineId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("İade miktarı 0'dan büyük olmalı.");
    }
}

public class RefundInvoiceRequestValidator : AbstractValidator<RefundInvoiceRequest>
{
    public RefundInvoiceRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("İade için en az bir satır seçin.");
        RuleForEach(x => x.Lines).SetValidator(new RefundLineRequestValidator());
    }
}

public class CreateInvoiceRequestValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceRequestValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Fatura en az bir satır içermeli.");
        RuleForEach(x => x.Lines).SetValidator(new CreateInvoiceLineRequestValidator());
        RuleFor(x => x.Payment!).SetValidator(new InvoicePaymentRequestValidator()).When(x => x.Payment is not null);
        RuleFor(x => x.RedeemPoints!.Value).GreaterThanOrEqualTo(0).WithMessage("Kullanılacak puan negatif olamaz.").When(x => x.RedeemPoints.HasValue);
    }
}
