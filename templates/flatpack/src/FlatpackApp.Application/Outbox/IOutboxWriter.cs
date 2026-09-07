namespace FlatpackApp.Application.Outbox;

public interface IOutboxWriter
{
    void Enqueue<T>(T message) where T : notnull;
}

public interface IOutboxHandler<in T> where T : notnull
{
    Task HandleAsync(T message, CancellationToken cancellationToken);
}

