#define AppName "Teplov Print for AutoCAD"
#define AppVersion "1.0.7"
#define AppPublisher "Вячеслав Теплов"

[Setup]
AppId={{F664E421-430B-471D-BF7F-70F4B7611B1F}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Teplov Print for AutoCAD
DisableProgramGroupPage=yes
UninstallDisplayName={#AppName}
OutputDir=output
OutputBaseFilename=Teplov-Print-AutoCAD-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Files]
Source: "PackageContents.xml"; DestDir: "{commonappdata}\Autodesk\ApplicationPlugins\TeplovPrint.bundle"; Flags: ignoreversion
Source: "..\bin-acad\*"; DestDir: "{commonappdata}\Autodesk\ApplicationPlugins\TeplovPrint.bundle\Contents\AutoCAD2015-2024"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,plot.log"
Source: "..\bin-acad2025-2027\*"; DestDir: "{commonappdata}\Autodesk\ApplicationPlugins\TeplovPrint.bundle\Contents\AutoCAD2025-2027"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,plot.log,runtimes\linux\native\*,runtimes\linux-arm\native\*,runtimes\linux-arm64\native\*,runtimes\osx-arm64\native\*,runtimes\osx-x64\native\*,runtimes\win-x86\native\*"
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion recursesubdirs createallsubdirs
