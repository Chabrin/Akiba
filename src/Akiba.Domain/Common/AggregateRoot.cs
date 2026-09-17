namespace Akiba.Domain.Common;

/// <summary>
/// Base class for aggregate roots: the entities that own a consistency boundary and are
/// loaded and saved as a unit.
/// </summary>
/// <typeparam name="TId">The aggregate's strongly typed identifier.</typeparam>
/// <remarks>
/// Aggregates have private setters and are mutated only through intention-revealing methods
/// - <c>loan.Restructure(...)</c>, never <c>loan.Balance = x</c>. The method name is what
/// tells a reader, and the audit trail, what actually happened.
/// </remarks>
public abstract class AggregateRoot<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected AggregateRoot(TId id) => Id = id;

    public TId Id { get; private set; }

    /// <summary>Events raised since this aggregate was loaded, in the order they happened.</summary>
    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    /// <summary>Called by the persistence layer once the events have been dispatched.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();
}
