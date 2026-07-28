#ifndef MyAppVersion
  #define MyAppVersion "1.2.0"
#endif

#define MyAppName "ExamTransfer"
#define MyAppPublisher "ExamTransfer"
#define MyClientExe "ExamTransfer.Desktop.exe"
#define MyServerExe "ExamTransfer.LocalServer.exe"

[Setup]
AppId={{724D43BD-E4C5-4927-A3CF-8AC292F03D21}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ExamTransfer
DefaultGroupName=ExamTransfer
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=ExamTransfer-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Client\{#MyClientExe}
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousSetupType=yes
UsePreviousTasks=yes
UsePreviousLanguage=yes
UsePreviousPrivileges=yes
CloseApplications=yes
RestartApplications=no

[Types]
Name: "teacher"; Description: "Máy giáo viên - Giao diện và Local Server"
Name: "student"; Description: "Máy học sinh - Chỉ cài giao diện"

[Components]
Name: "client"; Description: "Ứng dụng ExamTransfer"; Types: teacher student; Flags: fixed
Name: "server"; Description: "Local Server dành cho máy giáo viên"; Types: teacher

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng ngoài màn hình"; Flags: unchecked
Name: "startserver"; Description: "Tự mở Local Server khi đăng nhập Windows"; Components: server

[Files]
Source: "..\artifacts\release\Client\*"; DestDir: "{app}\Client"; Excludes: "publiccloud.runtime.json"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: client
Source: "..\artifacts\release\Client\publiccloud.runtime.json"; DestDir: "{app}\Client"; Attribs: readonly; Flags: ignoreversion; Components: client
Source: "..\artifacts\release\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: server
Source: "..\artifacts\release\release-manifest.json"; DestDir: "{app}"; Flags: ignoreversion; Components: client
Source: "..\scripts\installer-localserver-guard.ps1"; DestDir: "{app}\Support"; Flags: ignoreversion; Components: server
Source: "..\scripts\installer-localserver-guard.ps1"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\ExamTransfer"; Filename: "{app}\Client\{#MyClientExe}"; Components: client
Name: "{autodesktop}\ExamTransfer"; Filename: "{app}\Client\{#MyClientExe}"; Tasks: desktopicon; Components: client
Name: "{autoprograms}\ExamTransfer Local Server"; Filename: "{app}\Server\{#MyServerExe}"; Components: server
Name: "{userstartup}\ExamTransfer Local Server"; Filename: "{app}\Server\{#MyServerExe}"; Flags: runminimized; Tasks: startserver; Components: server

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer.LocalServer"" program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ExamTransfer TCP 5048"" dir=in action=allow protocol=TCP localport=5048 profile=private,domain remoteip=LocalSubnet program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 40550"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ExamTransfer UDP 40550"" dir=in action=allow protocol=UDP localport=40550 profile=private,domain remoteip=LocalSubnet program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden; Components: client; Check: IsStudentOnlyInstall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden; Components: client; Check: IsStudentOnlyInstall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 40550"""; Flags: runhidden; Components: client; Check: IsStudentOnlyInstall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer.LocalServer"" program=""{app}\Server\{#MyServerExe}"""; Flags: runhidden; Components: client; Check: IsStudentOnlyInstall
Filename: "{app}\Client\{#MyClientExe}"; Description: "Mở ExamTransfer"; Flags: nowait postinstall skipifsilent; Components: client

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Support\installer-localserver-guard.ps1"" -Mode StopOnly -InstalledServerPath ""{app}\Server\{#MyServerExe}"""; Flags: runhidden waituntilterminated; RunOnceId: "StopExactExamTransferServer"; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden; RunOnceId: "RemoveExamTransferTcpRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 40550"""; Flags: runhidden; RunOnceId: "RemoveExamTransferUdpRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden; RunOnceId: "RemoveLegacyExamTransferUdpRule"

[Code]
function IsStudentOnlyInstall: Boolean;
begin
  Result := not WizardIsComponentSelected('server');
end;

function RunLocalServerGuard(Mode: String; ManifestPath: String; var ResultCode: Integer): Boolean;
var
  ScriptPath: String;
  Parameters: String;
begin
  ExtractTemporaryFile('installer-localserver-guard.ps1');
  ScriptPath := ExpandConstant('{tmp}\installer-localserver-guard.ps1');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath +
    '" -Mode ' + Mode +
    ' -InstalledServerPath "' + ExpandConstant('{app}\Server\{#MyServerExe}') + '"';
  if ManifestPath <> '' then
    Parameters := Parameters + ' -ManifestPath "' + ManifestPath + '"';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not WizardIsComponentSelected('server') then
    exit;

  if not RunLocalServerGuard('StopAndPreflight', '', ResultCode) then
  begin
    Result := 'Không thể chạy kiểm tra Local Server trước khi cài đặt.';
    exit;
  end;

  case ResultCode of
    0: Result := '';
    41: Result := 'PORT_CONFLICT_TCP_5048: Cổng TCP 5048 đang bị tiến trình không thuộc ExamTransfer chiếm.';
    42: Result := 'PORT_CONFLICT_UDP_40550: Cổng UDP 40550 đang bị tiến trình không thuộc ExamTransfer chiếm.';
  else
    Result := 'Không thể dừng đúng Local Server đã cài đặt hoặc kiểm tra cổng thất bại.';
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsComponentSelected('server') then
      SetIniString('Install', 'Role', 'Teacher', ExpandConstant('{app}\install-role.ini'))
    else
      SetIniString('Install', 'Role', 'Student', ExpandConstant('{app}\install-role.ini'));

    if WizardIsComponentSelected('server') then
    begin
      if (not RunLocalServerGuard(
          'StartAndVerify',
          ExpandConstant('{app}\release-manifest.json'),
          ResultCode)) or (ResultCode <> 0) then
        RaiseException(
          'Cài đặt Local Server thất bại xác minh BuildId/ExamTransfer/2/UDP 40550. Mã lỗi: ' +
          IntToStr(ResultCode));
    end;

    if WizardIsComponentSelected('server') and (not WizardSilent) then
      MsgBox('Máy giáo viên phải đặt mạng Windows ở chế độ Private để học sinh trong cùng mạng có thể tìm thấy phòng.', mbInformation, MB_OK);
  end;
end;

// Không thêm [UninstallDelete] cho C:\ProgramData\ExamTransfer hoặc
// %LocalAppData%\ExamTransfer. Dữ liệu phải được giữ khi cập nhật/gỡ cài đặt.
