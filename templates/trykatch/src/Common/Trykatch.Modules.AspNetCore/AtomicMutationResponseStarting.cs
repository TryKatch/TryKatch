using System.Collections;
using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Trykatch.Modules.AspNetCore;

/// <summary>Defers application response-start callbacks until an atomic response is ready to publish.</summary>
internal sealed class StagedResponseStartingFeature : IHttpResponseFeature
{
    private readonly IHttpResponseFeature inner;
    private readonly GuardedHeaderDictionary headers;
    private readonly List<(Func<object, Task> Callback, object State)> startingCallbacks = [];
    private bool starting;
    private bool startingCompleted;
    private bool frozen;

    public StagedResponseStartingFeature(IHttpResponseFeature inner)
    {
        this.inner = inner;
        headers = new GuardedHeaderDictionary(this, () => this.inner.Headers);
    }

    public int StatusCode
    {
        get => inner.StatusCode;
        set { EnsureMutable(); inner.StatusCode = value; }
    }

    public string? ReasonPhrase
    {
        get => inner.ReasonPhrase;
        set { EnsureMutable(); inner.ReasonPhrase = value; }
    }

    public IHeaderDictionary Headers
    {
        get => headers;
        set
        {
            EnsureMutable();
            if (!ReferenceEquals(value, headers)) inner.Headers = value;
        }
    }

// IHttpResponseFeature still requires this legacy member; normal body access uses its separate feature.
#pragma warning disable CS0618
    public Stream Body
    {
        get => inner.Body;
        set { EnsureMutable(); inner.Body = value; }
    }
#pragma warning restore CS0618

    public bool HasStarted => inner.HasStarted;

    public void OnStarting(Func<object, Task> callback, object state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (startingCompleted || inner.HasStarted)
            throw new InvalidOperationException("Response-start callbacks cannot be registered after response preparation.");
        startingCallbacks.Add((callback, state));
    }

    public void OnCompleted(Func<object, Task> callback, object state) => inner.OnCompleted(callback, state);

    internal bool IsFrozen => frozen;

    internal void RegisterNativeFallback() => inner.OnStarting(
        static state => ((StagedResponseStartingFeature)state).FireStartingCallbacksAsync(), this);

    internal async Task FireStartingCallbacksAsync()
    {
        if (startingCompleted) return;
        if (starting) throw new InvalidOperationException("Response-start callbacks cannot recursively start the response.");

        starting = true;
        try
        {
            while (startingCallbacks.Count > 0)
            {
                int last = startingCallbacks.Count - 1;
                (Func<object, Task> callback, object state) = startingCallbacks[last];
                startingCallbacks.RemoveAt(last);
                await callback(state);
            }
            startingCompleted = true;
        }
        catch
        {
            // A failed callback must not leave callbacks that could run against an error response.
            startingCallbacks.Clear();
            startingCompleted = true;
            throw;
        }
        finally
        {
            starting = false;
        }
    }

    internal void Freeze() => frozen = true;
    internal void Unfreeze() => frozen = false;

    private void EnsureMutable()
    {
        if (frozen)
            throw new InvalidOperationException("The atomic response metadata is frozen for publication.");
    }

    private sealed class GuardedHeaderDictionary(
        StagedResponseStartingFeature owner,
        Func<IHeaderDictionary> getInner) : IHeaderDictionary
    {
        private IHeaderDictionary Inner => getInner();

        public StringValues this[string key]
        {
            get => Inner[key];
            set { owner.EnsureMutable(); Inner[key] = value; }
        }

        public long? ContentLength
        {
            get => Inner.ContentLength;
            set { owner.EnsureMutable(); Inner.ContentLength = value; }
        }

        public ICollection<string> Keys => Array.AsReadOnly(Inner.Keys.ToArray());
        public ICollection<StringValues> Values => Array.AsReadOnly(Inner.Values.ToArray());
        public int Count => Inner.Count;
        public bool IsReadOnly => owner.IsFrozen || Inner.IsReadOnly;

        public void Add(string key, StringValues value)
        {
            owner.EnsureMutable();
            // Preserve IDictionary.Add's duplicate-key behavior for this delegating collection.
#pragma warning disable ASP0019
            Inner.Add(key, value);
#pragma warning restore ASP0019
        }

        public void Add(KeyValuePair<string, StringValues> item)
        {
            owner.EnsureMutable();
            Inner.Add(item);
        }

        public void Clear()
        {
            owner.EnsureMutable();
            Inner.Clear();
        }

        public bool Contains(KeyValuePair<string, StringValues> item) => Inner.Contains(item);
        public bool ContainsKey(string key) => Inner.ContainsKey(key);
        public void CopyTo(KeyValuePair<string, StringValues>[] array, int arrayIndex) => Inner.ToArray().CopyTo(array, arrayIndex);
        public IEnumerator<KeyValuePair<string, StringValues>> GetEnumerator() => Inner.ToArray().AsEnumerable().GetEnumerator();
        public bool Remove(string key) { owner.EnsureMutable(); return Inner.Remove(key); }
        public bool Remove(KeyValuePair<string, StringValues> item) { owner.EnsureMutable(); return Inner.Remove(item); }
        public bool TryGetValue(string key, out StringValues value) => Inner.TryGetValue(key, out value);
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
