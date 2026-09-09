using FluentValidation;

namespace CloudPosGrid.Application.Modules.Staff;

public sealed class CreateStaffRequestValidator : AbstractValidator<CreateStaffRequest>
{
    public CreateStaffRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(256);
        // Personel hesabı da e-posta+şifre ile tam giriş yapabilir — kayıt ile aynı politika uygulanır.
        RuleFor(x => x.Password).NotEmpty()
            .MinimumLength(8).WithMessage("Şifre en az 8 karakter olmalı.")
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.")
            .MaximumLength(100);
        RuleFor(x => x.Role).NotEmpty();
        RuleFor(x => x.Pin!).Matches(@"^\d{4,6}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Pin))
            .WithMessage("PIN 4-6 haneli rakam olmalı.");
    }
}

public sealed class UpdateStaffRequestValidator : AbstractValidator<UpdateStaffRequest>
{
    public UpdateStaffRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Role).NotEmpty();
    }
}

public sealed class SetPinRequestValidator : AbstractValidator<SetPinRequest>
{
    public SetPinRequestValidator()
    {
        RuleFor(x => x.Pin!).Matches(@"^\d{4,6}$")
            .When(x => !string.IsNullOrWhiteSpace(x.Pin))
            .WithMessage("PIN 4-6 haneli rakam olmalı.");
    }
}
