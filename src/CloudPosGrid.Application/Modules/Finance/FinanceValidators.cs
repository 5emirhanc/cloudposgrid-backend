using FluentValidation;

namespace CloudPosGrid.Application.Modules.Finance;

public class CreateCashAccountRequestValidator : AbstractValidator<CreateCashAccountRequest>
{
    public CreateCashAccountRequestValidator() =>
        RuleFor(x => x.Name).NotEmpty().WithMessage("Kasa adı zorunlu.").MaximumLength(120);
}

public class UpdateCashAccountRequestValidator : AbstractValidator<UpdateCashAccountRequest>
{
    public UpdateCashAccountRequestValidator() =>
        RuleFor(x => x.Name).NotEmpty().WithMessage("Kasa adı zorunlu.").MaximumLength(120);
}

public class CreateFinanceTransactionRequestValidator : AbstractValidator<CreateFinanceTransactionRequest>
{
    public CreateFinanceTransactionRequestValidator()
    {
        RuleFor(x => x.CashAccountId).NotEmpty();
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Tutar 0'dan büyük olmalı.");
        RuleFor(x => x.Category).MaximumLength(120);
    }
}
