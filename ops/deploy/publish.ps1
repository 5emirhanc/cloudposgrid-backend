# =====================================================================
# CloudPosGrid — üretim derlemesi (Windows geliştirme makinesinde çalışır)
# API + Angular + Astro'yu derler, sunucuya kopyalanmaya hazır
# 3 klasörü  ..\..\deploy-artifacts\  altında toplar.
#
# Kullanım:  powershell -File ops\deploy\publish.ps1
# Sonra artifact'ları sunucuya at (örnek):
#   scp -r deploy-artifacts/* kullanici@sunucu:/tmp/cpg/
# Ayrıntı: DEPLOY.md
# =====================================================================
$ErrorActionPreference = "Stop"
$env:DOTNET_ROLL_FORWARD = "Major"   # makinede net9 yoksa net10 ile derle

# Yollar (bu script CloudPosGrid-backend\ops\deploy içinde)
$backend = Resolve-Path "$PSScriptRoot\..\.."
$root    = Resolve-Path "$backend\.."
$frontend = Join-Path $root "CloudPosGrid-frontend"
$promo    = Join-Path $root "Tanitim-sitesi"
$out     = Join-Path $backend "deploy-artifacts"

Write-Host "== Temizlik ==" -ForegroundColor Cyan
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $out | Out-Null

# --- 1) API (framework-dependent publish; sunucuda .NET 9 runtime kurulu olacak) ---
Write-Host "== API publish ==" -ForegroundColor Cyan
dotnet publish "$backend\src\CloudPosGrid.Api\CloudPosGrid.Api.csproj" `
    -c Release -o "$out\api" --nologo
if ($LASTEXITCODE -ne 0) { throw "API publish başarısız." }
# Geliştirme/örnek ortam dosyaları üretime gitmesin
Remove-Item "$out\api\appsettings.Development.json" -ErrorAction SilentlyContinue

# --- 2) Angular uygulaması (prod) ---
Write-Host "== Angular build ==" -ForegroundColor Cyan
Push-Location $frontend
npm ci
npx ng build --configuration production
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Angular build başarısız." }
Pop-Location
Copy-Item "$frontend\dist\cloudposgrid\browser" "$out\app" -Recurse

# --- 3) Tanıtım sitesi (Astro) ---
Write-Host "== Astro build ==" -ForegroundColor Cyan
Push-Location $promo
npm ci
npx astro build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Astro build başarısız." }
Pop-Location
Copy-Item "$promo\dist" "$out\www" -Recurse

# --- 4) Sunucu config dosyaları (nginx, systemd, env örneği, yedek script'i) ---
Write-Host "== Ops dosyaları ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$out\ops" | Out-Null
Copy-Item "$PSScriptRoot\*" "$out\ops\deploy" -Recurse -Exclude "publish.ps1"
Copy-Item "$backend\ops\backup-db.sh" "$out\ops\" -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "TAMAM. Artifact'lar: $out" -ForegroundColor Green
Write-Host "  api/  -> /var/www/cloudposgrid-api   (systemd servisi)" -ForegroundColor Gray
Write-Host "  app/  -> /var/www/cloudposgrid-app   (Angular)" -ForegroundColor Gray
Write-Host "  www/  -> /var/www/cloudposgrid-www   (Tanıtım)" -ForegroundColor Gray
Write-Host "Sonraki adımlar: DEPLOY.md" -ForegroundColor Gray
