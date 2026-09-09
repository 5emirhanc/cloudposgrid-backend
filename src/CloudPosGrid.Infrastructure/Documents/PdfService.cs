using CloudPosGrid.Application.Abstractions;
using CloudPosGrid.Application.Modules.Invoices;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace CloudPosGrid.Infrastructure.Documents;

/// <summary>
/// QuestPDF ile sunucu-taraflı belge üretimi (#24). Community lisansı Program.cs'te set edilir (KOBİ'ye ücretsiz).
/// </summary>
public sealed class PdfService : IPdfService
{
    public byte[] InvoicePdf(InvoiceDto invoice, string companyName, string currency)
    {
        string Money(decimal v) => $"{v:N2} {currency}";

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.6f, Unit.Centimetre);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor(Colors.Grey.Darken3));

                page.Header().Column(h =>
                {
                    h.Item().Text(companyName).Bold().FontSize(16).FontColor(Colors.Black);
                    h.Item().Text(invoice.Type == Domain.Enums.InvoiceType.Sales ? "SATIŞ FATURASI" : "ALIŞ FATURASI")
                        .SemiBold().FontSize(11).FontColor(Colors.Blue.Darken2);
                    h.Item().PaddingTop(4).Row(r =>
                    {
                        r.RelativeItem().Text($"No: {invoice.Number}");
                        r.RelativeItem().AlignRight().Text($"Tarih: {invoice.Date:dd.MM.yyyy}");
                    });
                    if (!string.IsNullOrWhiteSpace(invoice.ContactName))
                        h.Item().Text($"Cari: {invoice.ContactName}");
                    if (invoice.DueDate is { } due)
                        h.Item().Text($"Vade: {due:dd.MM.yyyy}");
                });

                page.Content().PaddingVertical(10).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(4); // ürün
                        c.RelativeColumn(1.2f); // miktar
                        c.RelativeColumn(1.6f); // birim fiyat
                        c.RelativeColumn(1.0f); // kdv
                        c.RelativeColumn(1.8f); // tutar
                    });

                    table.Header(header =>
                    {
                        void H(string txt, bool right = false)
                        {
                            var cell = header.Cell().Background(Colors.Grey.Lighten3).Padding(5);
                            (right ? cell.AlignRight() : cell.AlignLeft()).Text(txt).SemiBold();
                        }
                        H("Ürün"); H("Miktar", true); H("Birim Fiyat", true); H("KDV%", true); H("Tutar", true);
                    });

                    foreach (var l in invoice.Lines)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).Text(l.ProductName);
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).AlignRight().Text($"{l.Quantity:0.##}");
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).AlignRight().Text(Money(l.UnitPrice));
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).AlignRight().Text($"%{l.VatRate:0.##}");
                        table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(5).AlignRight().Text(Money(l.LineTotal + l.VatAmount));
                    }
                });

                page.Footer().Column(f =>
                {
                    f.Item().AlignRight().Text($"Ara Toplam: {Money(invoice.Subtotal)}");
                    f.Item().AlignRight().Text($"KDV: {Money(invoice.VatTotal)}");
                    if (invoice.Discount > 0m)
                        f.Item().AlignRight().Text($"Puan İndirimi: -{Money(invoice.Discount)}");
                    f.Item().AlignRight().Text($"GENEL TOPLAM: {Money(invoice.GrandTotal)}").Bold().FontSize(12).FontColor(Colors.Black);
                    f.Item().PaddingTop(8).AlignCenter().Text("CloudPosGrid ile üretildi").FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
        }).GeneratePdf();
    }
}
