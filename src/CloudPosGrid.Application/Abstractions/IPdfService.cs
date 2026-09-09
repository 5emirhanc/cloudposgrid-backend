using CloudPosGrid.Application.Modules.Invoices;

namespace CloudPosGrid.Application.Abstractions;

/// <summary>
/// Sunucu-taraflı PDF üretimi (#24) — fatura/teklif belgeleri. Tarayıcı yazdırmasından bağımsız, indirilebilir/
/// arşivlenebilir/WhatsApp'a gönderilebilir kurumsal belge. Implementasyon QuestPDF (yerel kütüphane) ile.
/// </summary>
public interface IPdfService
{
    /// <summary>Bir faturayı PDF'e dönüştürür (byte[]).</summary>
    byte[] InvoicePdf(InvoiceDto invoice, string companyName, string currency);
}
