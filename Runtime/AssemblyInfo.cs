using System.Runtime.CompilerServices;

// The tests drive the keyboard's reading themselves (WeftKeyboardInput), and the editor tools inspect Views.
[assembly: InternalsVisibleTo("Weft.Editor")]
[assembly: InternalsVisibleTo("Weft.Tests")]
[assembly: InternalsVisibleTo("Weft.Tests.InputSystem")]
