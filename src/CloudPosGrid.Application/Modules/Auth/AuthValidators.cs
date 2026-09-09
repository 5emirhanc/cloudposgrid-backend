using FluentValidation;

namespace CloudPosGrid.Application.Modules.Auth;

public class RegisterRequestValidator : AbstractValidator<RegisterRequest>
{
    public RegisterRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().WithMessage("İşletme adı zorunlu.").MaximumLength(200);
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Ad soyad zorunlu.").MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Geçerli bir e-posta girin.").MaximumLength(256);
        RuleFor(x => x.Password).NotEmpty()
            .MinimumLength(8).WithMessage("Şifre en az 8 karakter olmalı.")
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.")
            .MaximumLength(100);
    }
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public class SendCodeRequestValidator : AbstractValidator<SendCodeRequest>
{
    public SendCodeRequestValidator()
    {
        // Format + uzunluk denetlenmezse 256+ karakter e-posta varchar(256)'yı aşıp 400 yerine 500'e döner.
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Geçerli bir e-posta girin.").MaximumLength(256);
    }
}

public class ForgotPasswordRequestValidator : AbstractValidator<ForgotPasswordRequest>
{
    public ForgotPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Geçerli bir e-posta girin.").MaximumLength(256);
    }
}

public class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().WithMessage("Geçerli bir e-posta girin.").MaximumLength(256);
        RuleFor(x => x.Code).NotEmpty().Length(6).WithMessage("Kod 6 haneli olmalı.");
        RuleFor(x => x.NewPassword).NotEmpty()
            .MinimumLength(8).WithMessage("Şifre en az 8 karakter olmalı.")
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.")
            .MaximumLength(100);
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Mevcut şifre gerekli.");
        RuleFor(x => x.NewPassword).NotEmpty()
            .MinimumLength(8).WithMessage("Şifre en az 8 karakter olmalı.")
            .Matches("[A-Za-zÇĞİÖŞÜçğıöşü]").WithMessage("Şifre en az bir harf içermeli.")
            .Matches("[0-9]").WithMessage("Şifre en az bir rakam içermeli.")
            .MaximumLength(100);
        RuleFor(x => x.NewPassword).NotEqual(x => x.CurrentPassword).WithMessage("Yeni şifre mevcut şifreden farklı olmalı.");
    }
}
