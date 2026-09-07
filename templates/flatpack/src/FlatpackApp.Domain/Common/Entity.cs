namespace FlatpackApp.Domain.Common;

public abstract class Entity
{
    protected Entity(Guid id) => Id = id;

    public Guid Id { get; protected init; }
}

