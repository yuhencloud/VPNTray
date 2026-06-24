// Hand-authored global usings.
//
// The WPF markup-compile pass builds a temporary project (`*_wpftmp.csproj`)
// that does NOT pick up the SDK's auto-generated implicit-usings file, so any
// source file relying on ImplicitUsings (e.g. for `System.IO.Path`) fails the
// XAML build with CS0246. A hand-authored GlobalUsings.cs is a normal Compile
// item that the temp project DOES include, restoring those usings everywhere.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
