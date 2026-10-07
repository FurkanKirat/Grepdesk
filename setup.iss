; =======================================================
; Grepdesk Inno Setup Script
; =======================================================

[Setup]
AppId={{8B1A2C3D-4E5F-6A7B-8C9D-0E1F2A3B4C5D}
AppName=Grepdesk
AppVersion=1.0.0
AppPublisher=Grepdesk
AppPublisherURL=https://github.com/
DefaultDirName={autopf}\Grepdesk
DefaultGroupName=Grepdesk
OutputDir=Output
OutputBaseFilename=GrepdeskSetup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

; Yönetici izni gerekmeden de kurulabilsin (tercihen Program Files için yönetici ister)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Windows açıldığında otomatik başlat"; GroupDescription: "Ek Ayarlar:"; Flags: unchecked

[Files]
; dotnet publish çıktınızın bulunduğu dizini buraya yazın
Source: "Grepdesk.UI\bin\Release\net10.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Başlat menüsü kısayolu
Name: "{group}\Grepdesk"; Filename: "{app}\Grepdesk.UI.exe"
; Kaldırma (Uninstall) kısayolu
Name: "{group}\Grepdesk Kaldır"; Filename: "{uninstallexe}"
; Masaüstü kısayolu (Sadece task seçildiyse)
Name: "{autodesktop}\Grepdesk"; Filename: "{app}\Grepdesk.UI.exe"; Tasks: desktopicon

[Registry]
; Windows açılışında çalıştırma kaydı
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Grepdesk"; ValueData: """{app}\Grepdesk.UI.exe"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; Kurulum bittiğinde çalıştırma kutucuğu
Filename: "{app}\Grepdesk.UI.exe"; Description: "{cm:LaunchProgram,Grepdesk}"; Flags: nowait postinstall skipifsilent