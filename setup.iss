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
; GitHub release'lerindeki adla aynı: Grepdesk-v0.1.0-win-x64-Setup.exe
OutputBaseFilename=Grepdesk-v{#AppVersion}-win-x64-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible

; Yönetici izni gerekmeden de kurulabilsin (tercihen Program Files için yönetici ister)
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ChangesEnvironment=yes

[Languages]
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
turkish.AddToPath=Grepdesk CLI (grepdesk.exe) için kurulum klasörünü PATH ortam değişkenine ekle
english.AddToPath=Add installation directory to PATH for Grepdesk CLI (grepdesk.exe)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "addtopath"; Description: "{cm:AddToPath}"; Flags: unchecked

[Files]
; Önce: dotnet publish Grepdesk.UI -p:PublishProfile=win-x64
;       dotnet publish Grepdesk.Cli -p:PublishProfile=win-x64
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
Root: HKCU; Subkey: "Software\Classes\*\shell\Grepdesk"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\GrepdeskExtractHere"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\SystemFileAssociations\.zip\shell\GrepdeskExtractTo"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\*\shell\GrepdeskCompress"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GrepdeskCompress"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\Background\shell\GrepdeskPaste"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Directory\shell\GrepdeskPaste"; Flags: dontcreatekey uninsdeletekey
; "Birlikte aç" kaydı: dosya türü ve uygulama anahtarı. Uzantıların altına dağılan
; kayıtları (varsayılan uygulama seçimi dahil) [UninstallRun]'daki --unregister siler.
Root: HKCU; Subkey: "Software\Classes\Grepdesk.File"; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\Grepdesk.UI.exe"; Flags: dontcreatekey uninsdeletekey

[Run]
; Kurulum bittiğinde çalıştırma kutucuğu
Filename: "{app}\Grepdesk.UI.exe"; Description: "{cm:LaunchProgram,Grepdesk}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Dosyalar silinmeden önce: Grepdesk'i varsayılan uygulama yapan seçimleri ve
; uzantılara eklenen "Birlikte aç" kayıtlarını temizler. Yoksa o dosyalar silinmiş
; exe'yi açmaya çalışır ve çift tıklayınca hiçbir şey olmaz.
Filename: "{app}\Grepdesk.UI.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"

[Code]
const
  EnvironmentKey = 'Environment';

procedure AddDirToUserPath(Dir: string);
var
  Paths: string;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then
    Paths := '';
  if Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') > 0 then
    exit;
  if (Paths <> '') and (Paths[Length(Paths)] <> ';') then
    Paths := Paths + ';';
  Paths := Paths + Dir;
  RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', Paths);
end;

procedure RemoveDirFromUserPath(Dir: string);
var
  Paths, Remaining, Token, ResultPaths: string;
  SepPos: Integer;
begin
  if not RegQueryStringValue(HKCU, EnvironmentKey, 'Path', Paths) then
    exit;
  Remaining := Paths;
  ResultPaths := '';
  while Remaining <> '' do
  begin
    SepPos := Pos(';', Remaining);
    if SepPos > 0 then
    begin
      Token := Copy(Remaining, 1, SepPos - 1);
      Delete(Remaining, 1, SepPos);
    end
    else
    begin
      Token := Remaining;
      Remaining := '';
    end;
    if (Token <> '') and (CompareText(Token, Dir) <> 0) then
    begin
      if ResultPaths <> '' then
        ResultPaths := ResultPaths + ';';
      ResultPaths := ResultPaths + Token;
    end;
  end;
  if ResultPaths <> Paths then
    RegWriteExpandStringValue(HKCU, EnvironmentKey, 'Path', ResultPaths);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('addtopath') then
    AddDirToUserPath(ExpandConstant('{app}'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveDirFromUserPath(ExpandConstant('{app}'));
end;
