; =======================================================
; Grepdesk Inno Setup Script
; =======================================================

; Sürüm Directory.Build.props'tan gelir: publish edilen exe'den okunur
; ("1.0.0.0" -> "1.0.0"). Önce publish edilmeli, yoksa derleme hata verir.
#define AppExe "publish\win-x64\Grepdesk.UI.exe"
#define AppVersion RemoveFileExt(GetVersionNumbersString(AppExe))

[Setup]
AppId={{8B1A2C3D-4E5F-6A7B-8C9D-0E1F2A3B4C5D}
AppName=Grepdesk
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Furkan Kırat
AppPublisherURL=https://github.com/FurkanKirat/Grepdesk
AppSupportURL=https://github.com/FurkanKirat/Grepdesk/issues
AppUpdatesURL=https://github.com/FurkanKirat/Grepdesk/releases
UninstallDisplayIcon={app}\Grepdesk.UI.exe
DefaultDirName={autopf}\Grepdesk
DefaultGroupName=Grepdesk
OutputDir=Output
OutputBaseFilename=GrepdeskSetup-{#AppVersion}
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

[Files]
; Önce: dotnet publish Grepdesk.UI -p:PublishProfile=win-x64
; Self-contained: .NET runtime'ı içinde, makinede .NET kurulu olması gerekmez.
Source: "publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Başlat menüsü kısayolu
Name: "{group}\Grepdesk"; Filename: "{app}\Grepdesk.UI.exe"
; Kaldırma (Uninstall) kısayolu
Name: "{group}\Grepdesk Kaldır"; Filename: "{uninstallexe}"
; Masaüstü kısayolu (Sadece task seçildiyse)
Name: "{autodesktop}\Grepdesk"; Filename: "{app}\Grepdesk.UI.exe"; Tasks: desktopicon

[Registry]
; Eski kurulumların "Windows açıldığında başlat" kaydını temizler. Bu seçenek
; kaldırıldı: tray/arka plan modu olmadan açılışta sadece boş bir pencere açıyordu.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Grepdesk"; Flags: deletevalue uninsdeletevalue

; Explorer sağ tık menüsü kayıtları: uygulama Ayarlar'dan yazar, kurulum yazmaz
; (dontcreatekey). Kaldırırken silinir, yoksa menüde silinmiş exe'yi gösteren
; girdiler kalır. Liste WindowsShellIntegration ile aynı olmalı (test kontrol eder).
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\Grepdesk"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\Grepdesk"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\GrepdeskExtractHere"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\GrepdeskExtractTo"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\GrepdeskCompress"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GrepdeskCompress"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\GrepdeskPaste"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GrepdeskPaste"; Flags: dontcreatekey uninsdeletekey

[Run]
; Kurulum bittiğinde çalıştırma kutucuğu
Filename: "{app}\Grepdesk.UI.exe"; Description: "{cm:LaunchProgram,Grepdesk}"; Flags: nowait postinstall skipifsilent