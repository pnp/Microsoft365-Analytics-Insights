using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Declared here, not by the <UserSecretsId> MSBuild property, because this project sets
// GenerateAssemblyInfo=false - so the SDK never emits the attribute the property would normally
// produce, and the setting silently has no effect. This is the solution-wide developer secrets store
// that replaced the gitignored App.Debug.config; AnalyticsConfig reads it from THIS assembly so every
// executable and the test suite share one set of credentials.
[assembly: Microsoft.Extensions.Configuration.UserSecrets.UserSecretsId("pnp-m365-analytics-insights")]

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Common.Entities")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Common.Entities")]
[assembly: AssemblyCopyright("Copyright © __year__")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// Matches the convention already used by App.ControlPanel.Engine, the web-job engines and Web: internal
// helpers stay internal in the shipped API surface but can be asserted directly by the unit tests.
[assembly: InternalsVisibleTo("Tests.UnitTests")]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("637930bd-073b-421e-9f33-fe90bf2103c5")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
