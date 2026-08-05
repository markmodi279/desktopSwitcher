using System.Reflection;

// Assembly identity, and the whole of it.
//
// csc reads these attributes straight out of source and stamps a Win32 version resource
// from them, which is the only reason this works: there is no project file and no SDK
// here, and adding either would defeat the point of the app.
//
// Without this file the exe's Properties -> Details tab is blank. That is not cosmetic
// once a copy has been sitting in %LOCALAPPDATA% for six months: there is nothing on the
// file to say what it is, who built it, or whether it is the build you think it is, and
// the process in Task Manager is an unnamed .NET executable.
//
// ASCII only, like every other source file - see CLAUDE.md. The in-box compiler reads
// BOM-less files in the system codepage, and this text ends up in the shell's UI.

[assembly: AssemblyTitle("DesktopSwitcher")]
[assembly: AssemblyProduct("DesktopSwitcher")]
[assembly: AssemblyDescription("Numbered virtual desktop buttons docked in the Windows 10 taskbar.")]
[assembly: AssemblyCompany("Mark")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Mark")]

// M1 through M10 built the app; this is the first build that carries a version at all,
// so it starts where a first complete thing starts rather than pretending to a history
// it cannot show. Bump the file version per shipped build - it is what Explorer shows and
// what tells two copies apart. The assembly version binds references, and nothing
// references this assembly, so in practice it never moves.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
