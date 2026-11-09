# Gera os arquivos para quem só quer USAR o Seamline (sem ambiente de desenvolvimento):
#   saida\Seamline-Setup-<versão>.exe      instalador do Windows (o principal)
#   saida\Seamline-<versão>-portatil.zip   mesma coisa sem instalar: descompactar e abrir o Seamline.exe
#   saida\Seamline-<versão>-celular.apk    app do celular (opcional: o PC já instala sozinho)
#
# Precisa (só aqui, para gerar): Android Studio, .NET 10 SDK e Inno Setup 6.
# Uso, na pasta do projeto:   powershell -ExecutionPolicy Bypass -File instalador\gerar-instalador.ps1

$ErrorActionPreference = 'Stop'
$raiz = Split-Path -Parent $PSScriptRoot
$saida = Join-Path $PSScriptRoot 'saida'
$publicado = Join-Path $saida 'publicado'

function Passo($texto) { Write-Host ''; Write-Host "==> $texto" -ForegroundColor Cyan }
function Falhou($texto) { throw $texto }

# A versão vem do projeto do PC (<Version> no .csproj).
$csproj = Join-Path $raiz 'desktop\Seamline.Desktop\Seamline.Desktop.csproj'
$versao = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $versao) { Falhou 'Não achei a <Version> no Seamline.Desktop.csproj.' }
Write-Host "Seamline $versao"

# Ferramentas
$jbr = 'C:\Program Files\Android\Android Studio\jbr'
if (Test-Path $jbr) { $env:JAVA_HOME = $jbr }  # o Gradle não funciona com o Java 26 do PATH
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$platformTools = Join-Path $sdk 'platform-tools'
if (-not (Test-Path (Join-Path $platformTools 'adb.exe'))) { Falhou "adb não encontrado em $platformTools" }
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Falhou 'Inno Setup 6 não encontrado. Instale com: winget install JRSoftware.InnoSetup' }

# 1. Android: o server e o app (vão dentro do programa do PC)
Passo 'Compilando o Android (server e app)'
Push-Location (Join-Path $raiz 'android')
try {
    # --no-daemon: sem processos do Gradle sobrando depois (eles prendiam a saída do script)
    & .\gradlew.bat ':server:assembleDebug' ':app:assembleDebug' --console=plain --no-daemon
    if ($LASTEXITCODE -ne 0) { Falhou 'O Gradle falhou.' }
} finally { Pop-Location }

# 2. Windows: "self-contained" = leva o .NET junto, quem instala não precisa baixar nada
Passo 'Publicando o programa do PC (com o .NET embutido)'
if (Test-Path $publicado) { Remove-Item $publicado -Recurse -Force }
& dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publicado -p:DebugType=none --disable-build-servers
if ($LASTEXITCODE -ne 0) { Falhou 'O dotnet publish falhou.' }
foreach ($apk in 'seamline-server.apk', 'seamline-app.apk') {
    if (-not (Test-Path (Join-Path $publicado $apk))) { Falhou "$apk não foi copiado para a publicação." }
}

# 3. adb: é por ele que o PC fala com o celular (o aviso de licença vai junto)
Passo 'Copiando o adb'
$pastaAdb = New-Item -ItemType Directory -Force (Join-Path $publicado 'adb')
foreach ($arquivo in 'adb.exe', 'AdbWinApi.dll', 'AdbWinUsbApi.dll', 'NOTICE.txt') {
    Copy-Item (Join-Path $platformTools $arquivo) $pastaAdb
}

# 4. Instalador
Passo 'Gerando o instalador'
& $iscc "/DAppVersion=$versao" "/DSourceDir=$publicado" (Join-Path $PSScriptRoot 'Seamline.iss')
if ($LASTEXITCODE -ne 0) { Falhou 'O Inno Setup falhou.' }

# 5. Versão portátil e APK avulso
Passo 'Gerando o .zip portátil e o APK avulso'
$zip = Join-Path $saida "Seamline-$versao-portatil.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publicado '*') -DestinationPath $zip
Copy-Item (Join-Path $publicado 'seamline-app.apk') (Join-Path $saida "Seamline-$versao-celular.apk") -Force

Passo 'Pronto'
Get-ChildItem $saida -File | ForEach-Object { '{0,-40} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
