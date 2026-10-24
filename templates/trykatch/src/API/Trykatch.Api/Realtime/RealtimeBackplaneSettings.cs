using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text;

namespace Trykatch.Api.Realtime;

internal static class RealtimeBackplaneSettings
{
    internal static ConfigurationOptions Parse(string? value)
    {
        const string invalid = "Distributed realtime requires a valid authenticated realtime connection.";
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException(invalid);
        ConfigurationOptions connection;
        try { connection = ConfigurationOptions.Parse(value); }
        catch (ArgumentException) { throw new InvalidOperationException(invalid); }
        catch (FormatException) { throw new InvalidOperationException(invalid); }
        if (connection.EndPoints.Count != 1 || string.IsNullOrWhiteSpace(connection.Password)
            || !string.IsNullOrEmpty(connection.ServiceName)) throw new InvalidOperationException(invalid);
        connection.AbortOnConnectFail = false;
        connection.AllowAdmin = false;
        connection.ConnectRetry = 1;
        connection.ConnectTimeout = 1_000;
        connection.AsyncTimeout = 1_000;
        connection.SyncTimeout = 1_000;
        connection.IncludeDetailInExceptions = false;
        connection.IncludePerformanceCountersInExceptions = false;
        connection.ClientName = "realtime";
        return connection;
    }

    internal static RedisChannel ChannelPrefix(string applicationNamespace, string environment) =>
        RedisChannel.Literal("trykatch:realtime:" + applicationNamespace + ":" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(environment))).ToLowerInvariant());
}
