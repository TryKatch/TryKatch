#if TRYKATCH_STORAGE
using System.Globalization;
using Amazon.S3.Model;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Storage;

internal static class S3ConditionalObjectMetadata
{
    internal static void Write(PutObjectRequest request, ObjectWriteIdentity identity, bool cancellation)
    {
        request.Metadata.Add("tk-upload-version", "1");
        request.Metadata.Add("tk-upload-state", cancellation ? "cancelled" : "provisional");
        request.Metadata.Add("tk-operation-id", identity.OperationId.ToString("N"));
        request.Metadata.Add("tk-original-length", identity.OriginalLength.ToString(CultureInfo.InvariantCulture));
        request.Metadata.Add("tk-content-sha256", identity.Sha256);
        request.Metadata.Add("tk-metadata-fingerprint", identity.MetadataFingerprint);
    }

    internal static ConditionalObjectStat Read(GetObjectMetadataResponse response)
    {
        string? Get(string name)
        {
            string? key = response.Metadata.Keys.FirstOrDefault(key =>
                key.Equals("x-amz-meta-" + name, StringComparison.OrdinalIgnoreCase));
            return key is null ? null : response.Metadata[key];
        }
        string? state = Get("tk-upload-state");
        if (Get("tk-upload-version") != "1" || state is not ("provisional" or "cancelled")
            || !Guid.TryParseExact(Get("tk-operation-id"), "N", out Guid operationId)
            || !long.TryParse(Get("tk-original-length"), NumberStyles.None, CultureInfo.InvariantCulture, out long length)
            || !ConditionalObjectPolicy.TryIdentity(new(operationId, length, Get("tk-content-sha256")!,
                Get("tk-metadata-fingerprint")!), out ObjectWriteIdentity identity)
            || !ConditionalObjectPolicy.IsValidRevision(new(response.ETag)))
            return new(ConditionalObjectState.Unowned);
        long actual = response.Headers.ContentLength;
        bool cancellation = state == "cancelled";
        if (actual != (cancellation ? 0 : identity.OriginalLength)) return new(ConditionalObjectState.Unowned);
        return new(cancellation ? ConditionalObjectState.Cancellation : ConditionalObjectState.Provisional,
            actual, identity, new(response.ETag));
    }
}
#endif
