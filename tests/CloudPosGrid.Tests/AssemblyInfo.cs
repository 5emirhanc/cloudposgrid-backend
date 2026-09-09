using Xunit;

// Entegrasyon testleri TEK paylaşılan TestDb'yi kullanır ve eşzamanlı tenant/şema açar → paralel koşunca
// Postgres katalog unique index'lerinde (pg_type_typname_nsp_index, subscription_requests) yarışır ve
// bağlantı yük altında sıfırlanır (sahte hatalar). Testler DOĞRU; yalnız izole değiller → sıralı koştur.
// Assembly attribute derlemeye gömülür (xunit.runner.json kopya/okuma sorunlarından etkilenmez).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
