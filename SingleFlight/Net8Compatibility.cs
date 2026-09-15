// Compatibility shim for .NET 8: define Lock type used by the library
#if NET8_0
namespace SingleFlight
{
    // Minimal reference type used only as a lock target.
    // .NET 9+ provides a Lock type; for net8 we define a tiny stand-in so
    // the same source compiles across TFMs without changing behavior.
    internal sealed class Lock { }
}
#endif
