namespace Akiba.Domain;

/// <summary>
/// An empty anchor type used to reference the Akiba.Domain assembly without naming one of
/// its aggregates. Assembly scanning and the architecture tests bind to this rather than to
/// a real type, so that moving or renaming an aggregate never breaks wiring.
/// </summary>
public sealed class DomainAssemblyMarker
{
    private DomainAssemblyMarker()
    {
    }
}
