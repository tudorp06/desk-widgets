// The exe's identity, as Windows and every antivirus engine read it.
//
// Without these the compiler emits a version resource of 0.0.0.0 with no company, product or
// description, and File > Properties shows nothing. A small unsigned .NET exe with blank
// metadata is one of the things generic ML scanners score against, and the app has the rest of
// that profile honestly: it writes a Run key, updates itself and compiles community widgets at
// runtime. Filling this in does not make the exe trusted, but it removes one free strike and
// lets anyone confirm which build they have without running it.
//
// AssemblyVersion is the four-part shape (the three-part release number plus .0, the same
// convention as the MSIX). tools\set-version.ps1 keeps it in step with the other files.

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Desk Widgets")]
[assembly: AssemblyDescription("Lightweight desktop widgets for Windows.")]
[assembly: AssemblyProduct("Desk Widgets")]
[assembly: AssemblyCompany("Saiff")]
[assembly: AssemblyCopyright("Copyright (c) Saiff")]
[assembly: AssemblyVersion("1.10.1.0")]
[assembly: AssemblyFileVersion("1.10.1.0")]
[assembly: AssemblyInformationalVersion("1.10.1")]
[assembly: ComVisible(false)]
