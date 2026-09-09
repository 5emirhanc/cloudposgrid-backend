using CloudPosGrid.Domain.Common;
using CloudPosGrid.Domain.Enums;

namespace CloudPosGrid.Domain.Enums
{
    /// <summary>Kıymetli evrak tipi.</summary>
    public enum ChequeKind { Cheque, PromissoryNote } // Çek, Senet

    /// <summary>Yön: müşteriden ALINAN mı, tedarikçiye VERİLEN mi.</summary>
    public enum ChequeDirection { Received, Given }

    /// <summary>Yaşam döngüsü: portföyde / tahsil edildi / ciro edildi / karşılıksız / ödendi / iptal.</summary>
    public enum ChequeStatus { Portfolio, Collected, Endorsed, Bounced, Paid, Cancelled }
}

namespace CloudPosGrid.Domain.Entities
{
    /// <summary>
    /// Çek/senet portföyü (Türkiye B2B): alınan/verilen kıymetli evrak, vade + durum takibi.
    /// Vadesi yaklaşan/geçen ve karşılıksız evrak takibi; nakit akışı tahmini de bunları dikkate alabilir.
    /// </summary>
    public class Cheque : BaseEntity
    {
        public ChequeKind Kind { get; set; }
        public ChequeDirection Direction { get; set; }

        /// <summary>İlgili cari (borçlu/alacaklı). Opsiyonel.</summary>
        public Guid? ContactId { get; set; }
        /// <summary>Cari adı anlık kopyası (cari silinse de evrak listesi okunur kalsın).</summary>
        public string? ContactName { get; set; }

        public decimal Amount { get; set; }
        public DateTime DueDate { get; set; }

        public string? Bank { get; set; }
        public string? SerialNo { get; set; }

        public ChequeStatus Status { get; set; } = ChequeStatus.Portfolio;
        public string? Note { get; set; }
        public Guid? BranchId { get; set; }
    }
}
