; ============================================================
;  Renova - Inno Setup Script
;  Gere com: powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1
;  O instalador sai na raiz do projeto: Renova-Setup-<versao>.exe
;
;  Assistente classico, so com "Avancar" e "Instalar":
;    Boas-vindas > Licenca > Pasta de destino > Opcoes > Pronto para instalar > Concluido
; ============================================================

#define AppName           "Renova"
#define AppExeName        "Renova.exe"
#ifndef AppVersion
  #define AppVersion      "3.0.0"
#endif
#define AppPublisher      "CyberThrust"
#define AppURL            "https://cyberthrust.io/"
#define AppId             "{{1637D3D8-1959-4FD7-86E1-5FA89AAFB230}"
; Instalacoes antigas, quando o produto se chamava Curupira (removidas ao instalar o Renova)
#define LegacyAppId       "{A2E1D6C8-9B4E-4F91-9F0E-7A55D0F1B3C4}"
#define PublishDir        "..\src\Renova\bin\publish"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription=Instalador do {#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; Paginas do assistente: Avancar, Avancar, Instalar
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
DisableReadyPage=no
DisableFinishedPage=no
ShowLanguageDialog=no
; Instalador gerado na raiz do projeto
OutputDir=..
OutputBaseFilename=Renova-Setup-{#AppVersion}
SetupIconFile=..\src\Renova\Assets\Renova.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
#ifdef TESTE
; Somente para testar o assistente sem administrador: ISCC /DTESTE
PrivilegesRequired=lowest
#else
PrivilegesRequired=admin
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
DirExistsWarning=no
CloseApplications=yes
RestartApplications=no
LicenseFile=EULA.rtf
; Atalho na inicializacao e pasta de dados sao do usuario que instala (intencional)
UsedUserAreasWarning=no

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english";    MessagesFile: "compiler:Default.isl"

[Messages]
portuguese.WelcomeLabel2=Este assistente vai instalar o [name/ver] no seu computador.%n%nO Renova reúne atualização do Windows e dos aplicativos, limpeza, desempenho e correção de vulnerabilidades num painel só.%n%nFeche os outros programas antes de continuar e clique em Avançar.

[Tasks]
Name: "desktopicon";  Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupentry"; Description: "Abrir o {#AppName} ao entrar no Windows"; GroupDescription: "Integração"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*";              DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"
Source: "..\README.md";                 DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*";                    DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";             Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";       Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";       Filename: "{app}\{#AppExeName}"; Tasks: startupentry

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Abrir o {#AppName} agora"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Renova"

[Code]
const
  LegacyKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#LegacyAppId}_is1';

{ Remove a instalacao antiga (Curupira) antes de copiar o Renova.
  A pasta de dados e movida antes, porque o desinstalador antigo apagaria
  logs, backups .reg e reports. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Uninst, DadosAntigos, DadosNovos: String;
  Code: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKLM, LegacyKey, 'UninstallString', Uninst) then
    Exit;

  DadosAntigos := ExpandConstant('{localappdata}\Curupira');
  DadosNovos   := ExpandConstant('{localappdata}\Renova');
  if DirExists(DadosAntigos) and not DirExists(DadosNovos) then
    RenameFile(DadosAntigos, DadosNovos);

  Exec(RemoveQuotes(Uninst), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE,
       ewWaitUntilTerminated, Code);
end;
