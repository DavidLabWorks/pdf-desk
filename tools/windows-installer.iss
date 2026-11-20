; Pass /DArch=x64 or /DArch=arm64 and /DPayload=<self-contained publish directory>.
#define AppVersion GetVersionNumbersString(Payload + "\PDFDesk.exe")
[Setup]
AppId=DavidLabWorks.PDFDesk
AppName=PDF Desk
AppVersion={#AppVersion}
AppPublisher=DavidLabWorks
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\PDF Desk
PrivilegesRequired=admin
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.17763
UninstallDisplayIcon={app}\PDFDesk.exe
SetupIconFile={#Payload}\Assets\pdfdesk.ico
OutputDir=..\artifacts\installers\{#Copy(AppVersion, 1, RPos(".", AppVersion) - 1)}
OutputBaseFilename=PDFDesk-Setup-{#Arch}
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
DisableDirPage=no
DisableProgramGroupPage=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\PDF Desk"; Filename: "{app}\PDFDesk.exe"
Name: "{commondesktop}\PDF Desk"; Filename: "{app}\PDFDesk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PDFDesk.exe"; Description: "{cm:LaunchProgram,PDF Desk}"; Flags: postinstall nowait skipifsilent
