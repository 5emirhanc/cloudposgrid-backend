# CloudPosGrid API — Render / konteyner dağıtımı için çok aşamalı imaj.
#
# TÜRKÇE KÜLTÜR NOTU (önemli): taban imaj bilerek Debian tabanlı `aspnet:9.0`. Alpine ya da
# "extra-slim" varyantlarında ICU yoktur; InvariantGlobalization açık kalırsa ₺ biçimi, tarih
# biçimi ve Türkçe'nin noktalı/noktasız i kuralları SESSİZCE bozulur (çökme olmaz, veri bozulur).
#
# BELLEK NOTU: ücretsiz katmanlar 512 MB RAM verir. ASP.NET Core varsayılan olarak Server GC
# kullanır ve çekirdek başına ayrı yığın ayırır → bu boyutta bellek şişer. Workstation GC'ye
# çekiyoruz (DOTNET_gcServer=0).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Önce yalnız csproj'lar: bağımlılıklar değişmedikçe restore katmanı önbellekten gelir (hızlı deploy).
COPY src/CloudPosGrid.Domain/CloudPosGrid.Domain.csproj                 src/CloudPosGrid.Domain/
COPY src/CloudPosGrid.Application/CloudPosGrid.Application.csproj       src/CloudPosGrid.Application/
COPY src/CloudPosGrid.Infrastructure/CloudPosGrid.Infrastructure.csproj src/CloudPosGrid.Infrastructure/
COPY src/CloudPosGrid.Api/CloudPosGrid.Api.csproj                       src/CloudPosGrid.Api/
RUN dotnet restore src/CloudPosGrid.Api/CloudPosGrid.Api.csproj

COPY src/ src/
RUN dotnet publish src/CloudPosGrid.Api/CloudPosGrid.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5

# Bilgilendirme amaçlı; gerçek port çalışma anında PORT ortam değişkeninden okunur (Program.cs).
EXPOSE 8080

ENTRYPOINT ["dotnet", "CloudPosGrid.Api.dll"]
