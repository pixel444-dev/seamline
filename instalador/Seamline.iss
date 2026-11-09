; Instalador do Seamline para Windows (Inno Setup 6: https://jrsoftware.org/isinfo.php).
; Não rode este arquivo direto: use gerar-instalador.ps1, que compila tudo antes e passa
; a versão (AppVersion) e a pasta com os arquivos prontos (SourceDir).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "saida\publicado"
#endif

[Setup]
; Identificador fixo: é por ele que o Windows sabe que uma versão nova substitui a antiga.
AppId={{6F1E3B2A-8C4D-4E7F-9A1B-5D2C3E4F6A70}
AppName=Seamline
AppVersion={#AppVersion}
AppPublisher=pixel444-dev
AppPublisherURL=https://github.com/pixel444-dev/seamline
AppSupportURL=https://github.com/pixel444-dev/seamline/issues
DefaultDirName={autopf}\Seamline
DefaultGroupName=Seamline
DisableProgramGroupPage=yes
; Instala só para o usuário atual (não pede administrador), mas deixa escolher "para todos".
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; O app usa APIs do Windows 10 versão 2004 (build 19041) em diante, só em 64 bits.
MinVersion=10.0.19041
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\desktop\Seamline.Desktop\Assets\seamline.ico
UninstallDisplayIcon={app}\Seamline.exe
WizardStyle=modern
OutputDir=saida
OutputBaseFilename=Seamline-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; Fecha o Seamline aberto antes de atualizar os arquivos.
CloseApplications=yes

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Seamline"; Filename: "{app}\Seamline.exe"
Name: "{autodesktop}\Seamline"; Filename: "{app}\Seamline.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Seamline.exe"; Description: "{cm:LaunchProgram,Seamline}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Para o adb que veio junto, senão o desinstalador não consegue apagar a pasta dele.
Filename: "{app}\adb\adb.exe"; Parameters: "kill-server"; Flags: runhidden skipifdoesntexist; RunOnceId: "PararAdb"
