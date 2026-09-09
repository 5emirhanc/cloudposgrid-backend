namespace CloudPosGrid.Domain.Enums;

/// <summary>Kullanıcı rolleri (yetkilendirme). Owner=sahip, Admin=yönetici, Accountant=muhasebe,
/// Cashier=kasiyer, Waiter=garson, Staff=genel personel.</summary>
public enum UserRole { Owner = 0, Admin = 1, Accountant = 2, Staff = 3, Cashier = 4, Waiter = 5 }

/// <summary>Abonelik planı. Starter=deneme/varsayılan, Pro=Profesyonel, Enterprise=Kurumsal, Chain=Zincir (çok şube).</summary>
public enum TenantPlan { Starter = 0, Pro = 1, Enterprise = 2, Chain = 3 }

/// <summary>
/// İşletme tipi (sektör). UI, terminoloji, navigasyon ve varsayılan akış buna göre uyarlanır.
/// Hospitality = kafe/bar/restoran, Retail = bakkal/market, Service = tamirci/usta, Beauty = kuaför.
/// </summary>
public enum BusinessType { General = 0, Hospitality = 1, Retail = 2, Service = 3, Beauty = 4 }

/// <summary>İşletme (tenant) durumu.</summary>
public enum TenantStatus { Trial = 0, Active = 1, Suspended = 2, Cancelled = 3 }

/// <summary>Abonelik faturalama döngüsü.</summary>
public enum BillingCycle { Monthly = 0, Yearly = 1 }

/// <summary>Paket yükseltme talebi durumu (havale onay akışı).</summary>
public enum SubscriptionRequestStatus { Pending = 0, Approved = 1, Rejected = 2 }

/// <summary>Stok hareket tipi.</summary>
public enum StockMovementType { In = 0, Out = 1, Adjustment = 2 }

/// <summary>Stok hareketinin kaynağı.</summary>
public enum StockMovementReference { Manual = 0, Purchase = 1, Sale = 2, Adjustment = 3, Transfer = 4, Waste = 5 }

/// <summary>Tedarikçi (satın alma) siparişi durumu. Mal kabul edildikçe kısmi/tam teslim edilmişe geçer.</summary>
public enum PurchaseOrderStatus { Draft = 0, Sent = 1, PartiallyReceived = 2, Received = 3, Cancelled = 4 }

/// <summary>Teklif durumu. "Expired" BİLEREK yok — süresi geçme ValidUntil'den TÜRETİLİR (DTO'da IsExpired),
/// çünkü onu güncelleyecek arka plan işi olmadan enum değeri gerçekle uyumsuz kalırdı.</summary>
public enum QuoteStatus { Draft = 0, Sent = 1, Accepted = 2, Rejected = 3, Converted = 4 }

/// <summary>Terazi barkoduna gömülü değerin ne olduğu: tartılan AĞIRLIK mı, hesaplanmış FİYAT mı.</summary>
public enum ScaleEmbedMode { Weight = 0, Price = 1 }

/// <summary>Kasiyerin satış anında uyguladığı elle indirimin gerekçesi (denetim izi + indirim raporu için).</summary>
public enum DiscountReason { Complimentary = 0, Damaged = 1, Rounding = 2, Negotiation = 3, Other = 9 }

/// <summary>Fire/zayi nedeni (bozulan, kırılan, son kullanma, ikram).</summary>
public enum WasteReason { Spoiled = 0, Broken = 1, Expired = 2, Complimentary = 3, Other = 4 }

/// <summary>Cari tipi.</summary>
public enum ContactType { Customer = 0, Supplier = 1, Both = 2 }

/// <summary>Cari hareket yönü. Debit = borç (cari bize daha çok borçlanır), Credit = alacak (borç azalır).</summary>
public enum TransactionDirection { Debit = 0, Credit = 1 }

/// <summary>Kasa/banka hesap tipi.</summary>
public enum CashAccountType { Cash = 0, Bank = 1 }

/// <summary>Gelir/gider tipi.</summary>
public enum FinanceType { Income = 0, Expense = 1 }

/// <summary>Fatura tipi.</summary>
public enum InvoiceType { Sales = 0, Purchase = 1 }

/// <summary>Fatura durumu.</summary>
public enum InvoiceStatus { Draft = 0, Issued = 1, Paid = 2, Cancelled = 3 }

/// <summary>Ödeme yöntemi.</summary>
public enum PaymentMethod { Cash = 0, Card = 1, Transfer = 2, Credit = 3 }

/// <summary>Ödeme yönü. In = tahsilat (para girişi), Out = ödeme (para çıkışı).</summary>
public enum PaymentDirection { In = 0, Out = 1 }

/// <summary>Stok sayım oturumu durumu. Open = taslak (sayım sürüyor), Applied = uygulandı, Cancelled = iptal.</summary>
public enum StockCountStatus { Open = 0, Applied = 1, Cancelled = 2 }

/// <summary>Adisyon (sipariş) tipi. Service = servis/tamir iş emri.</summary>
public enum OrderType { DineIn = 0, Takeaway = 1, Delivery = 2, Service = 3 }

/// <summary>Kasa vardiyası durumu.</summary>
public enum CashShiftStatus { Open = 0, Closed = 1 }

/// <summary>Servis iş emri iş akışı durumu.</summary>
public enum WorkStatus { Received = 0, InProgress = 1, Ready = 2 }

/// <summary>Randevu durumu (kuaför/güzellik).</summary>
public enum AppointmentStatus { Scheduled = 0, Done = 1, Cancelled = 2 }

/// <summary>Adisyon durumu.</summary>
public enum OrderStatus { Open = 0, Closed = 1, Cancelled = 2 }

/// <summary>Adisyonun kaynağı: personel (POS), müşteri (QR menü) ya da pazaryeri (Trendyol vb.).</summary>
public enum OrderSource { Pos = 0, Qr = 1, Marketplace = 2 }

/// <summary>Çekilen pazaryeri siparişinin sisteme aktarım durumu.
/// Pending: dedupe yuvası iddia edildi ama satış/eşleştirme henüz işlenmedi (çökme sonrası reconcile toplar).
/// Cancelled: pazaryeri siparişi iptal/iade edildi → yerel satış (varsa) void'lendi (idempotent terminal durum).</summary>
public enum MarketplaceOrderSyncStatus { Imported = 0, NeedsMapping = 1, Error = 2, Pending = 3, Cancelled = 4 }

/// <summary>Ürünün pazaryerindeki ilan durumu. External: ilan bizde açılmadı (elle açılmış, sadece stok eşleşmesi).
/// Draft→Submitted→(Approved|Rejected); Failed: gönderim hatası.</summary>
public enum MarketplaceListingStatus { External = 0, Draft = 1, Submitted = 2, Approved = 3, Rejected = 4, Failed = 5 }
