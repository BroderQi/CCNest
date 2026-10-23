; CCNest installer (Inno Setup 6).
; Build: scripts\build.ps1  (publishes the app, then runs ISCC with /DAppVersion=...)
;
; Design notes
;  * Per-machine install into Program Files (admin) so the binaries that the elevated helper runs
;    cannot be replaced by a standard-user process.
;  * User data (%LOCALAPPDATA%\CCNest: settings, encrypted secrets, the permanent dedicated browser
;    profile, logs) is never touched by install, upgrade or uninstall.
;  * Uninstall removes any CCNest WFP rules left behind (the uninstaller already runs elevated).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\CCNest"
#endif

[Setup]
AppId={{6A3C0D1E-6C2B-4A8E-9F0E-0C0E5E7C4A10}
AppName=CCNest
AppVersion={#AppVersion}
AppVerName=CCNest {#AppVersion}
AppPublisher=CCNest
AppComments=Claude 环境管家
VersionInfoVersion={#AppVersion}
VersionInfoDescription=CCNest Setup
DefaultDirName={autopf}\CCNest
DefaultGroupName=CCNest
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=CCNest-Setup-{#AppVersion}-x64
SetupIconFile=..\src\CCNest.App\Assets\ccnest.ico
UninstallDisplayIcon={app}\CCNest.exe
UninstallDisplayName=CCNest
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
chinesesimp.DesktopIcon=创建桌面快捷方式
chinesesimp.BrowserIcon=创建“Claude专属浏览器”桌面快捷方式
chinesesimp.LaunchApp=启动 CCNest
chinesesimp.BrowserShortcut=Claude专属浏览器 (CCNest)
chinesesimp.DataKept=卸载不会删除你的设置、加密保存的凭据和 Claude 专属浏览器资料（位于 %LOCALAPPDATA%\CCNest）。如需彻底删除，请在卸载后手动删除该文件夹。
english.DesktopIcon=Create a desktop shortcut
english.BrowserIcon=Create a "Claude dedicated browser" desktop shortcut
english.LaunchApp=Launch CCNest
english.BrowserShortcut=Claude Browser (CCNest)
english.DataKept=Uninstalling keeps your settings, encrypted credentials and the dedicated browser profile (%LOCALAPPDATA%\CCNest). Delete that folder manually to remove them.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "browsericon"; Description: "{cm:BrowserIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\CCNest"; Filename: "{app}\CCNest.exe"
Name: "{autoprograms}\{cm:BrowserShortcut}"; Filename: "{app}\CCNest.exe"; Parameters: "--open-browser"
Name: "{autodesktop}\CCNest"; Filename: "{app}\CCNest.exe"; Tasks: desktopicon
Name: "{autodesktop}\{cm:BrowserShortcut}"; Filename: "{app}\CCNest.exe"; Parameters: "--open-browser"; Tasks: browsericon

[Run]
; Start as the original (non-elevated) user: CCNest itself always runs as a standard user.
Filename: "{app}\CCNest.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Remove any CCNest WFP rules that may remain after a crash (no-op when there are none).
Filename: "{app}\CCNest.exe"; Parameters: "--restore-network"; Flags: runhidden waituntilterminated; RunOnceId: "CCNestRemoveWfp"

[UninstallDelete]
; Elevated-helper copy of the core (admin-only folder). User data under %LOCALAPPDATA% is kept.
Type: filesandordirs; Name: "{commonappdata}\CCNest\core"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox(CustomMessage('DataKept'), mbInformation, MB_OK);
end;
