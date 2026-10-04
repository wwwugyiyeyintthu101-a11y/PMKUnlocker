#ifndef SourceDir
  #error SourceDir must point to a verified release candidate
#endif
#ifndef OutputDir
  #define OutputDir "output"
#endif
#define AppVersion GetVersionNumbersString(SourceDir + "\PMKUnlocker.dll")

[Setup]
AppId={{A2AB8E74-CFA8-4809-9348-B52356F73CD6}
AppName=PMK Mobile Tool
AppVersion={#AppVersion}
AppPublisher=PMK
DefaultDirName={localappdata}\Programs\PMKMobileTool
DefaultGroupName=PMK Mobile Tool
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\PMKUnlocker.exe
OutputDir={#OutputDir}
OutputBaseFilename=PMKMobileTool-{#AppVersion}-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Icons]
Name: "{group}\PMK Mobile Tool"; Filename: "{app}\PMKUnlocker.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\PMK Mobile Tool"; Filename: "{app}\PMKUnlocker.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Code]
function InitializeSetup(): Boolean;
var
  RuntimeRoot: String;
  Found: TFindRec;
  HasRuntime: Boolean;
begin
  RuntimeRoot := ExpandConstant('{commonpf32}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*');
  HasRuntime := False;
  if FindFirst(RuntimeRoot, Found) then
  begin
    try
      repeat
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          HasRuntime := FileExists(ExpandConstant('{commonpf32}\dotnet\shared\Microsoft.WindowsDesktop.App\') + Found.Name + '\System.Windows.Forms.dll');
      until HasRuntime or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
  Result := HasRuntime;
  if not Result then
    MsgBox('Install Microsoft .NET 8 Desktop Runtime (x86) first, then run this installer again. The x64 runtime alone cannot run this app.', mbError, MB_OK);
end;
