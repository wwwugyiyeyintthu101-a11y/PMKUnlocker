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
// The app is x86, so it needs the 32-bit Desktop runtime - the x64 one cannot run it. That runtime is not
// always under Program Files (x86): a per-user install (the dotnet-install script, winget, an SDK laid out
// per user) puts it under %LOCALAPPDATA%\Microsoft, and a machine can run the app only because DOTNET_ROOT(x86)
// points there. Probing one folder made a working machine look broken, so every place the host will look is
// probed, and a miss asks instead of refusing: the operator knows their machine.
function RuntimeInRoot(Root: String): Boolean;
var
  Base: String;
  Found: TFindRec;
begin
  Result := False;
  if Root = '' then Exit;
  Base := AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App';
  if not DirExists(Base) then Exit;
  if FindFirst(Base + '\8.*', Found) then
  begin
    try
      repeat
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          if FileExists(Base + '\' + Found.Name + '\System.Windows.Forms.dll') then
            Result := True;
      until Result or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
end;

function HasDesktopRuntime(): Boolean;
begin
  Result :=
    RuntimeInRoot(ExpandConstant('{commonpf32}\dotnet')) or
    RuntimeInRoot(ExpandConstant('{localappdata}\Microsoft\dotnet')) or
    RuntimeInRoot(ExpandConstant('{localappdata}\Microsoft\dotnet-x86')) or
    RuntimeInRoot(GetEnv('DOTNET_ROOT(x86)')) or
    RuntimeInRoot(GetEnv('DOTNET_ROOT'));
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if HasDesktopRuntime() then Exit;
  if MsgBox('PMK Mobile Tool needs the 32-bit (x86) .NET 8 Desktop Runtime, and it was not found in the usual'
            + #13#10 + 'places. The x64 runtime alone cannot run this app.'
            + #13#10#13#10 + 'Download (Desktop Runtime, x86):'
            + #13#10 + 'https://dotnet.microsoft.com/download/dotnet/8.0/runtime/desktop/x86'
            + #13#10#13#10 + 'Continue the install anyway?', mbConfirmation, MB_YESNO) = IDNO then
    Result := False;
end;
