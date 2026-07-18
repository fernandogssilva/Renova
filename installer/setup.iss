; ============================================================
;  SysMaintenanceHub - Inno Setup Script
;  Compile com: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss
;  Requer: publicar antes com scripts\publish.ps1
; ============================================================

#define AppName           "SysMaintenanceHub"
#define AppExeName        "SysMaintenanceHub.exe"
#define AppVersion        "1.0.0"
#define AppPublisher      "DataSec"
#define AppURL            "https://datasec.com.br/"
#define AppId             "{{A2E1D6C8-9B4E-4F91-9F0E-7A55D0F1B3C4}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=no
OutputDir=output
OutputBaseFilename=SysMaintenanceHub-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
AllowNoIcons=yes
DirExistsWarning=no
CloseApplications=yes
RestartApplications=no
LicenseFile=EULA.rtf
InfoBeforeFile=

[Languages]
Name: "portuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english";    MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";     Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1
Name: "startupentry";    Description: "Executar {#AppName} no login do Windows"; GroupDescription: "Integração"; Flags: unchecked

[Files]
; Publique com "scripts\publish.ps1" antes — a saída fica em src\SysMaintenanceHub\bin\publish\
Source: "..\src\SysMaintenanceHub\bin\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\SysMaintenanceHub\bin\publish\*";              DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md";                                        DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*";                                           DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}";              Filename: "{app}\{#AppExeName}"
Name: "{group}\Desinstalar {#AppName}";  Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}";        Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}";        Filename: "{app}\{#AppExeName}"; Tasks: startupentry

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Iniciar {#AppName}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\SysMaintenanceHub"

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsAdmin then
  begin
    MsgBox('Este instalador requer privilégios de administrador.', mbError, MB_OK);
    Result := False;
  end;
end;
