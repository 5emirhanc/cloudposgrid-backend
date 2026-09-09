using FluentValidation;

namespace CloudPosGrid.Application.Modules.Contacts;

public class CreateContactRequestValidator : AbstractValidator<CreateContactRequest>
{
    public CreateContactRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Cari adı zorunlu.").MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
        RuleFor(x => x.TaxNo).MaximumLength(40);
        RuleFor(x => x.Phone).MaximumLength(40);
    }
}

public class UpdateContactRequestValidator : AbstractValidator<UpdateContactRequest>
{
    public UpdateContactRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Cari adı zorunlu.").MaximumLength(200);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public class CreateAccountTransactionRequestValidator : AbstractValidator<CreateAccountTransactionRequest>
{
    public CreateAccountTransactionRequestValidator() =>
        RuleFor(x => x.Amount).GreaterThan(0).WithMessage("Tutar 0'dan büyük olmalı.");
}
