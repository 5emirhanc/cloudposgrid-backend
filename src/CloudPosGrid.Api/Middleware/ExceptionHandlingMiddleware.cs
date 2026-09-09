using CloudPosGrid.Application.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CloudPosGrid.Api.Middleware;

/// <summary>Uygulama hatalarını tutarlı ProblemDetails yanıtına çevirir.</summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppException ex)
        {
            await WriteAsync(context, ex.StatusCode, ex.Message, ex.Code);
        }
        catch (FluentValidation.ValidationException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                string.Join(" | ", ex.Errors.Select(e => e.ErrorMessage)));
        }
        catch (DbUpdateConcurrencyException)
        {
            // Aynı kaydı (ör. ürün stoğu) eşzamanlı değiştirme — aşırı satışı önler.
            await WriteAsync(context, StatusCodes.Status409Conflict,
                "Kayıt bu sırada başka bir işlem tarafından değiştirildi. Lütfen tekrar deneyin.");
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // İstemci isteği iptal etti (bağlantı koptu) — hata değil, sessizce geç (log kirletme).
            if (!context.Response.HasStarted)
                context.Response.StatusCode = 499; // Client Closed Request
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Beklenmeyen hata");
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "Sunucu hatası oluştu.");
        }
    }

    private static async Task WriteAsync(HttpContext context, int status, string detail, string? code = null)
    {
        if (context.Response.HasStarted) return;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = status,
            Title = status >= 500 ? "Sunucu Hatası" : "İstek Hatası",
            Detail = detail,
        };
        if (code is not null) problem.Extensions["code"] = code;
        await context.Response.WriteAsJsonAsync(problem);
    }
}
