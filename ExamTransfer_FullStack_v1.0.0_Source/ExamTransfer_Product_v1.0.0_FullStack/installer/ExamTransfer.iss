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
Source: "..\artifacts\release\Client\*"; DestDir: "{app}\Client"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: client
Source: "..\artifacts\release\Server\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: server

[Icons]
Name: "{autoprograms}\ExamTransfer"; Filename: "{app}\Client\{#MyClientExe}"; Components: client
Name: "{autodesktop}\ExamTransfer"; Filename: "{app}\Client\{#MyClientExe}"; Tasks: desktopicon; Components: client
Name: "{autoprograms}\ExamTransfer Local Server"; Filename: "{app}\Server\{#MyServerExe}"; Components: server
Name: "{userstartup}\ExamTransfer Local Server"; Filename: "{app}\Server\{#MyServerExe}"; Flags: runminimized; Tasks: startserver; Components: server

[Run]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ExamTransfer TCP 5048"" dir=in action=allow protocol=TCP localport=5048 profile=private remoteip=LocalSubnet"; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden; Components: server
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ExamTransfer UDP 5050"" dir=in action=allow protocol=UDP localport=5050 profile=private remoteip=LocalSubnet"; Flags: runhidden; Components: server
Filename: "{app}\Server\{#MyServerExe}"; Description: "Khởi động ExamTransfer Local Server"; Flags: nowait postinstall skipifsilent; Components: server
Filename: "{app}\Client\{#MyClientExe}"; Description: "Mở ExamTransfer"; Flags: nowait postinstall skipifsilent; Components: client

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#MyServerExe}"; Flags: runhidden; RunOnceId: "StopExamTransferServer"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer TCP 5048"""; Flags: runhidden; RunOnceId: "RemoveExamTransferTcpRule"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ExamTransfer UDP 5050"""; Flags: runhidden; RunOnceId: "RemoveExamTransferUdpRule"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsComponentSelected('server') and (not WizardSilent) then
    MsgBox('Máy giáo viên phải đặt mạng Windows ở chế độ Private để học sinh trong cùng mạng có thể tìm thấy phòng.', mbInformation, MB_OK);
end;

; Không thêm [UninstallDelete] cho C:\ProgramData\ExamTransfer hoặc
; %LocalAppData%\ExamTransfer. Dữ liệu phải được giữ khi cập nhật/gỡ cài đặt.
