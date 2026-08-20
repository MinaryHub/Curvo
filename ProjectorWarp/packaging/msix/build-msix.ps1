# Curvo 의 MSIX 패키지를 만든다.
#   pwsh packaging\msix\build-msix.ps1 -Version 1.3.0
#
# 스토어가 수집할 때 다시 서명하므로 제출에는 서명이 필요하지 않다. 이 PC 에서 사이드로드로
# 시험하려면 Identity/@Publisher 와 같은 주체를 가진 인증서로 서명하고 그 인증서를 신뢰해야
# 한다(끝에 안내가 출력된다).
param(
    [string]$Version = "1.3.0",
    # 사이드로드 시험용. 스토어는 PLACEHOLDER 가 남은 패키지를 반려하므로, 명시하지 않으면
    # 패킹을 거부한다.
    [switch]$AllowPlaceholders
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
    $stage = Join-Path $root 'msix-stage'
    $pub = Join-Path $root 'publish-msix'
    Remove-Item $stage, $pub -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host "Publishing $Version ..." -ForegroundColor Cyan
    # 단일 파일이 아닌 폴더 배치로 낸다. MSIX 자체가 컨테이너이고, 펼쳐진 배치가 시작이 빠르며
    # 스토어 인증 도구가 실제 파일을 들여다볼 수 있다.
    dotnet publish -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version -p:AssemblyVersion=$Version `
        -p:FileVersion=$Version -p:InformationalVersion=$Version `
        -o $pub
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
    Remove-Item (Join-Path $pub '*.pdb') -ErrorAction SilentlyContinue

    Write-Host "Staging package layout ..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    Copy-Item (Join-Path $pub '*') $stage -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $stage 'Assets') -Recurse -Force

    $manifest = Join-Path $stage 'AppxManifest.xml'
    Copy-Item (Join-Path $PSScriptRoot 'Package.appxmanifest') $manifest -Force

    # <Identity> 안의 Version 만 바꾼다. 앵커 없이 Version="[0-9.]+" 로 잡으면
    # TargetDeviceFamily 의 MinVersion 까지 앱 버전으로 덮어써 최소 OS 버전이 깨진다.
    # -creplace 는 대소문자를 구분하므로 <?xml version="1.0"?> 의 소문자 version 은 건드리지 않는다.
    # UTF-8 without BOM: MakeAppx 는 UTF-16 본문과 일부 SDK 에서 BOM 을 거부한다.
    $stamped = (Get-Content $manifest -Raw) -creplace '(<Identity[^>]*?Version=")[0-9.]+(")', "`${1}$Version.0`$2"
    [System.IO.File]::WriteAllText($manifest, $stamped, (New-Object System.Text.UTF8Encoding($false)))

    # 실제 식별자 값만 본다. 파일 상단 주석에도 PLACEHOLDER 라는 낱말이 있을 수 있다.
    $placeholders = Select-String -Path $manifest -Pattern '(Name|Publisher)="[^"]*PLACEHOLDER|<PublisherDisplayName>\s*PLACEHOLDER'
    if ($placeholders) {
        $lines = ($placeholders | ForEach-Object { "    line $($_.LineNumber): $($_.Line.Trim())" }) -join "`n"
        if (-not $AllowPlaceholders) {
            throw @"
AppxManifest still has PLACEHOLDER identity values, so this package would be rejected by the Store:
$lines
Fill them in from Partner Center > Product > Product identity, or pass -AllowPlaceholders to
build an unsubmittable package for local sideload testing.
"@
        }
        Write-Warning "Packing with PLACEHOLDER identity values (-AllowPlaceholders). Do NOT submit this package."
    }

    # 매니페스트가 가리키는 실행 파일이 실제로 담겼는지 확인한다. 어셈블리 이름을 바꿨는데
    # 매니페스트를 못 고치면 설치는 되고 실행만 안 되는 패키지가 나온다.
    $exeName = ([xml](Get-Content $manifest -Raw)).Package.Applications.Application.Executable
    if (-not (Test-Path (Join-Path $stage $exeName))) {
        throw "AppxManifest points at $exeName but the staged payload has no such file."
    }

    $makeappx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $makeappx) { throw "makeappx.exe not found. Install the Windows 10/11 SDK." }

    $suffix = if ($placeholders) { '-PLACEHOLDER-do-not-submit' } else { '' }
    $out = Join-Path $root "Curvo-$Version$suffix.msix"
    Write-Host "Packing $out ..." -ForegroundColor Cyan
    & $makeappx.FullName pack /d $stage /p $out /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

    Write-Host "`nBuilt $out" -ForegroundColor Green
    Write-Host @"

Next steps:
  * Store submission: upload this .msix in Partner Center (it re-signs it for you).
  * Local sideload test: sign it first, e.g.
      `$cert = New-SelfSignedCertificate -Type Custom -Subject "<same as Identity/@Publisher>" ``
                 -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My ``
                 -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
      signtool sign /fd SHA256 /a /f <exported.pfx> /p <pw> "$out"
    then trust the cert in LocalMachine\TrustedPeople and: Add-AppxPackage "$out"
"@ -ForegroundColor DarkGray
}
finally {
    Pop-Location
}
