using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CloudPosGrid.Api.Filters;

/// <summary>
/// Action argümanları için kayıtlı FluentValidation doğrulayıcılarını otomatik çalıştırır.
/// Hata varsa 400 ValidationProblemDetails döner.
/// </summary>
public sealed class ValidationActionFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var sp = context.HttpContext.RequestServices;

        foreach (var argument in context.ActionArguments.Values)
        {
            if (argument is null) continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(argument.GetType());
            if (sp.GetService(validatorType) is IValidator validator)
            {
                var ctx = new ValidationContext<object>(argument);
                var result = await validator.ValidateAsync(ctx, context.HttpContext.RequestAborted);
                if (!result.IsValid)
                {
                    var errors = result.Errors
                        .GroupBy(e => e.PropertyName)
                        .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

                    context.Result = new BadRequestObjectResult(
                        new ValidationProblemDetails(errors) { Title = "Doğrulama hatası", Status = 400 });
                    return;
                }
            }
        }

        await next();
    }
}
