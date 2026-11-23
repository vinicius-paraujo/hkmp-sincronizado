namespace System.Runtime.CompilerServices {
    /// <summary>
    /// Lets the compiler build records and init-only properties on .NET Framework, which does not define this type.
    /// HKMP gets it from an assembly the game provides; the production sources compiled here need it too.
    /// </summary>
    internal static class IsExternalInit {
    }
}
