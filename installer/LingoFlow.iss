#define AppVersion "0.2.4"
#define PublishDirectory "..\dist\LingoFlow-" + AppVersion
[Setup]
AppId={{A33C1CF6-E5C9-4D5C-B461-C01EB9AD4E19}
AppName=译流 LingoFlow
AppVersion={#AppVersion}
AppPublisher=LingoFlow
DefaultDirName={localappdata}\Programs\LingoFlow
DefaultGroupName=译流 LingoFlow
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir=..\dist
OutputBaseFilename=LingoFlow-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\LingoFlow.exe
SetupIconFile=..\src\LingoFlow\Assets\lingoflow-v2.ico
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDirectory}\LingoFlow.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDirectory}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDirectory}\Assets\*"; DestDir: "{app}\Assets"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishDirectory}\Dependencies\*"; DestDir: "{app}\Dependencies"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\译流 LingoFlow"; Filename: "{app}\LingoFlow.exe"
Name: "{autodesktop}\译流 LingoFlow"; Filename: "{app}\LingoFlow.exe"; Tasks: desktopicon
Name: "{group}\{cm:UninstallProgram,译流 LingoFlow}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\LingoFlow.exe"; Description: "{cm:LaunchProgram,译流 LingoFlow}"; Flags: nowait postinstall skipifsilent

