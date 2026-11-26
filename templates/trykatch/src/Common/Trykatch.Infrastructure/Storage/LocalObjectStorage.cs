using Microsoft.Extensions.Configuration;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Storage;

internal sealed class LocalObjectStorage : IObjectStorage, IConditionalObjectStorage
{
    private readonly string root;
    private readonly string namespaceFingerprint;

    public LocalObjectStorage(IConfiguration configuration)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuration["Storage:LocalPath"]
            ?? Path.Combine(AppContext.BaseDirectory, "storage")));
        namespaceFingerprint = ConditionalObjectPolicy.NamespaceFingerprint("local-v1", root);
    }

    public string NamespaceFingerprint => namespaceFingerprint;

    public async Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length,
        string contentType, ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConditionalObjectPolicy.IsValidKey(key)
            || !ConditionalObjectPolicy.TryIdentity(identity, out ObjectWriteIdentity normalizedIdentity)
            || length != normalizedIdentity.OriginalLength
            || !ConditionalObjectPolicy.IsValidContentType(contentType))
            return Result(ConditionalObjectWriteDisposition.InvalidPayload);

        using CancellationTokenSource timeout = new(ConditionalObjectPolicy.OperationTimeout);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // Read and verify before locking. A cancellation sentinel can therefore win while a slow source is spooling.
            await using VerifiedObjectPayload payload = await VerifiedObjectPayload.CreateAsync(content, length,
                normalizedIdentity, operation.Token);
            await using FileStream objectLock = await LocalObjectLock.AcquireAsync(root, key, operation.Token);
            string path = Resolve(key);
            ConditionalObjectStat existing = await ReadStatAsync(path, operation.Token);
            if (existing.State == ConditionalObjectState.Unavailable)
                return Result(ConditionalObjectWriteDisposition.Unavailable, existing);
            if (existing.State != ConditionalObjectState.Missing)
                return ConditionalObjectPolicy.Existing(existing, normalizedIdentity);

            ObjectStorageRevision revision = NewRevision();
            try
            {
                await PublishAsync(path, ConditionalObjectState.Provisional, length, normalizedIdentity, revision,
                    payload.Stream, overwrite: false, operation.Token);
                return Result(ConditionalObjectWriteDisposition.Written,
                    new(ConditionalObjectState.Provisional, length, normalizedIdentity, revision));
            }
            catch (ObjectPublishRaceException)
            {
                ConditionalObjectStat raced = await ReadStatAsync(path, operation.Token);
                return raced.State == ConditionalObjectState.Unavailable
                    ? Result(ConditionalObjectWriteDisposition.Unavailable, raced)
                    : ConditionalObjectPolicy.Existing(raced, normalizedIdentity);
            }
        }
        catch (InvalidDataException)
        {
            return Result(ConditionalObjectWriteDisposition.InvalidPayload);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result(ConditionalObjectWriteDisposition.Unavailable);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Result(ConditionalObjectWriteDisposition.Unavailable);
        }
    }

    public async Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConditionalObjectPolicy.IsValidKey(key)) return new(ConditionalObjectState.Unowned);
        using CancellationTokenSource timeout = new(ConditionalObjectPolicy.OperationTimeout);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await ReadStatAsync(Resolve(key), operation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStorageFailure(exception) || exception is OperationCanceledException)
        {
            return new(ConditionalObjectState.Unavailable);
        }
    }

    public async Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key,
        ObjectStorageRevision? expectedVersion, ObjectWriteIdentity identity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConditionalObjectPolicy.IsValidKey(key)
            || !ConditionalObjectPolicy.TryIdentity(identity, out ObjectWriteIdentity normalizedIdentity)
            || !ConditionalObjectPolicy.IsValidRevision(expectedVersion))
            return Result(ConditionalObjectWriteDisposition.InvalidPayload);

        using CancellationTokenSource timeout = new(ConditionalObjectPolicy.OperationTimeout);
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using FileStream objectLock = await LocalObjectLock.AcquireAsync(root, key, operation.Token);
            string path = Resolve(key);
            ConditionalObjectStat current = await ReadStatAsync(path, operation.Token);
            if (current.State == ConditionalObjectState.Unavailable)
                return Result(ConditionalObjectWriteDisposition.Unavailable, current);
            if (current.State == ConditionalObjectState.Cancellation)
            {
                return ConditionalObjectPolicy.SameIdentity(current.Identity, normalizedIdentity)
                    ? Result(ConditionalObjectWriteDisposition.AlreadyExists, current)
                    : Result(ConditionalObjectWriteDisposition.Conflict, current);
            }

            bool fenceMissing = current.State == ConditionalObjectState.Missing && expectedVersion is null;
            bool replaceOwned = current.State == ConditionalObjectState.Provisional
                && expectedVersion is not null
                && ConditionalObjectPolicy.SameIdentity(current.Identity, normalizedIdentity)
                && string.Equals(current.Revision?.Value, expectedVersion.Value, StringComparison.Ordinal);
            if (!fenceMissing && !replaceOwned)
                return Result(ConditionalObjectWriteDisposition.Conflict, current);

            ObjectStorageRevision revision = NewRevision();
            try
            {
                await PublishAsync(path, ConditionalObjectState.Cancellation, 0, normalizedIdentity, revision,
                    payload: null, overwrite: replaceOwned, operation.Token);
                return Result(ConditionalObjectWriteDisposition.Written,
                    new(ConditionalObjectState.Cancellation, 0, normalizedIdentity, revision));
            }
            catch (ObjectPublishRaceException)
            {
                ConditionalObjectStat raced = await ReadStatAsync(path, operation.Token);
                if (raced.State == ConditionalObjectState.Cancellation
                    && ConditionalObjectPolicy.SameIdentity(raced.Identity, normalizedIdentity))
                    return Result(ConditionalObjectWriteDisposition.AlreadyExists, raced);
                return raced.State == ConditionalObjectState.Unavailable
                    ? Result(ConditionalObjectWriteDisposition.Unavailable, raced)
                    : Result(ConditionalObjectWriteDisposition.Conflict, raced);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Result(ConditionalObjectWriteDisposition.Unavailable);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Result(ConditionalObjectWriteDisposition.Unavailable);
        }
    }

    public async Task PutAsync(string key, Stream content, long contentLength, string contentType,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(key);
        RefuseReservedRawPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using FileStream destination = File.Create(path);
        await content.CopyToAsync(destination, cancellationToken);
    }

    public Task<Stream> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(key);
        if (IsReservedPath(path))
        {
            FileStream envelope = new(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            try
            {
                if (!LocalObjectEnvelope.TryRead(envelope, out ConditionalObjectStat snapshot))
                    throw new InvalidDataException("Managed object envelope is invalid.");
                return Task.FromResult<Stream>(new LocalObjectPayloadReadStream(envelope, snapshot.Length!.Value));
            }
            catch
            {
                envelope.Dispose();
                throw;
            }
        }
        return Task.FromResult<Stream>(File.OpenRead(path));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(key);
        RefuseReservedRawPath(path);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static async Task<ConditionalObjectStat> ReadStatAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using FileStream source = new(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            return LocalObjectEnvelope.TryRead(source, out ConditionalObjectStat stat)
                ? stat : new(ConditionalObjectState.Unowned);
        }
        catch (FileNotFoundException) { return new(ConditionalObjectState.Missing); }
        catch (DirectoryNotFoundException) { return new(ConditionalObjectState.Missing); }
    }

    private async Task PublishAsync(string targetPath, ConditionalObjectState state, long currentLength,
        ObjectWriteIdentity identity, ObjectStorageRevision revision, Stream? payload, bool overwrite,
        CancellationToken cancellationToken)
    {
        string staging = Path.Combine(root, "_module-uploads", "v1", ".staging");
        Directory.CreateDirectory(staging);
        string temporary = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await LocalObjectEnvelope.WriteAsync(temporary, state, currentLength, identity, revision, payload,
                cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            try
            {
                File.Move(temporary, targetPath, overwrite);
            }
            catch (IOException exception) when (File.Exists(targetPath))
            {
                throw new ObjectPublishRaceException(exception);
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (FileNotFoundException) { }
        }
    }

    private string Resolve(string key)
    {
        string separatorsNormalized = key.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(root, separatorsNormalized.TrimStart(Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Object key escapes the configured storage root.");
        return path;
    }

    private void RefuseReservedRawPath(string path)
    {
        if (IsReservedPath(path)) throw new InvalidOperationException("The managed upload namespace is reserved.");
    }

    private bool IsReservedPath(string path)
    {
        string relative = Path.GetRelativePath(root, path);
        string first = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return string.Equals(first, "_module-uploads", StringComparison.OrdinalIgnoreCase);
    }

    private static ObjectStorageRevision NewRevision() => new(Guid.NewGuid().ToString("N"));
    private static ConditionalObjectWriteResult Result(ConditionalObjectWriteDisposition disposition,
        ConditionalObjectStat? snapshot = null) => new(disposition, snapshot);
    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException
        or TimeoutException or InvalidOperationException or ArgumentException;

    private sealed class ObjectPublishRaceException(IOException innerException)
        : IOException("The object destination changed during publication.", innerException);
}
