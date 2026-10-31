using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// In SDK-style projects such as this one, several assembly attributes that were historically
// defined in this file are now automatically added during build and populated with
// values defined in project properties. For details of which attributes are included
// and how to customise this process see: https://aka.ms/assembly-info-properties


// Setting ComVisible to false makes the types in this assembly not visible to COM
// components.  If you need to access a type in this assembly from COM, set the ComVisible
// attribute to true on that type.

[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM.

[assembly: Guid("aa6052a6-647a-498d-86d0-4cce487747a9")]

[assembly: InternalsVisibleTo("ACadSharp.Tests, PublicKey=0024000004800000940000000602000000240000525341310004000001000100797f50c32548b3da2e9fbed30dd824d12b4bd2c4c4234625816497391179ba3fdf24be77efd3d126c428502b5e4e5210708e930cba514bb2dee0b9acb8fc3d7991d768fc24e3d33117aeb9941a07a49effb37eb6c36255dfd64677b7a4fab07c4e82ca9ba0d0854e4149e5e7a267ed3e674222590fff083d8414e5bc723f83cf")]
[assembly: InternalsVisibleTo("ACadSharp.Formats, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b148ab602997d7649ec11cf13981ab8cf9518f0ecdc7cde5f458efb8b94bdbb9c553bc6f2c65fb293bacb5661a374250f1792183b475b91c69eb26ed6eed3094caddf4957f8bfef2ac0ae127ff4e2134d9411b4cad6e83e1d2a0a3aec4c16771d8ad4b31d89099d2d4521b3fd05fb3e1bf1981045c7241e9cbbf65d6cd64fcbd")]
[assembly: InternalsVisibleTo("ACadSharp.Formats.Tests, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b148ab602997d7649ec11cf13981ab8cf9518f0ecdc7cde5f458efb8b94bdbb9c553bc6f2c65fb293bacb5661a374250f1792183b475b91c69eb26ed6eed3094caddf4957f8bfef2ac0ae127ff4e2134d9411b4cad6e83e1d2a0a3aec4c16771d8ad4b31d89099d2d4521b3fd05fb3e1bf1981045c7241e9cbbf65d6cd64fcbd")]

// [PATCH] Allows the GDB2CAD downstream (Gdb2CadLib's DwgStreamExporter / DwgEntityWkb) to reuse
// the internal encoders (DwgObjectWriter / CadTemplate). This library has strong-name signing
// (SignAssembly) enabled, so the friend assemblies must be strong-named with the same key pair
// (GDB2CAD repo: src\Gdb2Cad.snk, public key below; token d162c9a1fb19e3).
[assembly: InternalsVisibleTo("Gdb2CadLib, PublicKey=002400000480000094000000060200000024000052534131000400000100010001d20f1103f6cb34212ab879ec83e83f6975c59afa0402e96228c7454e5c50fabe9b4531f93ca8eed2f42fd65a0b73c41ab5cf3395501d2ce7a99179e5ef6402b9dafdba47e193d9766d153640d7a48a6af25c38db26c3c253c22817cada29841bf7e3561de7680f77aae3929b8dc344cab9e47d4bcdd19ea8097f0b506f3dfc")]
[assembly: InternalsVisibleTo("GDB2CAD.Tests, PublicKey=002400000480000094000000060200000024000052534131000400000100010001d20f1103f6cb34212ab879ec83e83f6975c59afa0402e96228c7454e5c50fabe9b4531f93ca8eed2f42fd65a0b73c41ab5cf3395501d2ce7a99179e5ef6402b9dafdba47e193d9766d153640d7a48a6af25c38db26c3c253c22817cada29841bf7e3561de7680f77aae3929b8dc344cab9e47d4bcdd19ea8097f0b506f3dfc")]
