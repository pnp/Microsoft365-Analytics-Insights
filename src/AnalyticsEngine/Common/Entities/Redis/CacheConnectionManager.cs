﻿using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Net;
using System.Threading.Tasks;

namespace Common.Entities.Redis
{
    /// <summary>
    /// Wrapper class for redis connection. Tries key-based auth first when the connection string
    /// includes a password; otherwise (or on failure) authenticates via Entra ID / RBAC using the
    /// runtime service principal.
    /// </summary>
    /// <remarks>
    /// New Azure Managed Redis databases provisioned by this installer ship with access keys
    /// disabled (RBAC-only) and the App Service connection string is built without a
    /// <c>password=</c> segment, so the RBAC path becomes the primary path on fresh deployments.
    /// Existing installs with keyed caches keep working unchanged.
    /// <para>
    /// The RBAC path uses <see cref="AzureCacheForRedis.ConfigureForAzureWithServicePrincipalAsync"/>
    /// from <c>Microsoft.Azure.StackExchangeRedis</c>, which proactively refreshes the Entra
    /// bearer token before it expires and re-authenticates the multiplexer on reconnect — without
    /// this the multiplexer would silently start failing all commands ~60–90 min after process
    /// start with <c>MicrosoftEntraAuthenticationFailure</c> on the server and <c>SocketClosed</c>
    /// / <c>RedisTimeoutException</c> on the client.
    /// </para>
    /// </remarks>
    public class CacheConnectionManager
    {
        #region Singleton

        readonly ConnectionMultiplexer _muxer = null;
        readonly IDatabase _conn = null;
        private CacheConnectionManager(ConnectionMultiplexer muxer)
        {
            _muxer = muxer;
            _conn = _muxer.GetDatabase();
        }

        private static CacheConnectionManager _connectionManager = null;
        private static readonly object _lock = new object();

        /// <summary>
        /// Gets or creates a singleton connection manager. When the connection string contains a
        /// <c>password=</c> the key-based path is attempted first; otherwise the RBAC path is
        /// taken straight away. RBAC requires the runtime account credentials.
        /// </summary>
        public static CacheConnectionManager GetConnectionManager(string connectionString, ILogger logger = null, string tenantId = null, string clientId = null, string clientSecret = null)
        {
            if (_connectionManager == null)
            {
                lock (_lock)
                {
                    if (_connectionManager == null)
                    {
                        _connectionManager = CreateConnectionManager(connectionString, logger, tenantId, clientId, clientSecret);
                    }
                }
            }

            return _connectionManager;
        }

        private static CacheConnectionManager CreateConnectionManager(string connectionString, ILogger logger, string tenantId, string clientId, string clientSecret)
        {
            // Parse the caller's connection string just to inspect whether a password was provided.
            // The connect attempts below re-parse it so they each get a clean ConfigurationOptions.
            var parsedOptions = ConfigurationOptions.Parse(connectionString);
            var hasPassword = !string.IsNullOrEmpty(parsedOptions.Password);

            if (hasPassword)
            {
                try
                {
                    var keyOptions = ConfigurationOptions.Parse(connectionString);
                    keyOptions.ConnectTimeout = 15000;
                    keyOptions.SyncTimeout = 15000;
                    keyOptions.AsyncTimeout = 15000;
                    var muxer = ConnectionMultiplexer.Connect(keyOptions);
                    muxer.GetDatabase().Ping();
                    logger?.LogInformation("Redis connected using key-based authentication.");
                    return new CacheConnectionManager(muxer);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning($"Redis key-based auth failed ({ex.Message}). Attempting RBAC/Entra ID auth...");
                }
            }
            else
            {
                var localNoAuthOptions = ConfigurationOptions.Parse(connectionString);
                if (HasLoopbackEndpoint(localNoAuthOptions))
                {
                    try
                    {
                        localNoAuthOptions.ConnectTimeout = 15000;
                        localNoAuthOptions.SyncTimeout = 15000;
                        localNoAuthOptions.AsyncTimeout = 15000;
                        var localMuxer = ConnectionMultiplexer.Connect(localNoAuthOptions);
                        localMuxer.GetDatabase().Ping();
                        logger?.LogInformation("Redis connected to loopback endpoint without authentication.");
                        return new CacheConnectionManager(localMuxer);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning($"Redis loopback no-auth connect failed ({ex.Message}). Attempting RBAC/Entra ID auth...");
                    }
                }

                logger?.LogInformation("Redis connection string has no password — using RBAC/Entra ID auth with runtime service principal.");
            }

            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Redis RBAC/Entra ID auth cannot proceed: runtime account credentials (tenantId, clientId, clientSecret) are not configured." +
                    (hasPassword ? " Key-based auth also failed (see warning above)." : string.Empty));
            }

            try
            {
                // ConfigureForAzure* methods are async (they acquire the initial bearer token and register
                // handlers to refresh it before expiry). We're inside a sync singleton-init path that runs
                // once per process under _lock — wrap in Task.Run to detach from any ASP.NET / OWIN sync
                // context and avoid the classic sync-over-async deadlock.
                var rbacMuxer = Task.Run(() => ConnectWithRbacAsync(connectionString, tenantId, clientId, clientSecret)).GetAwaiter().GetResult();
                PingWithRetryForPolicyPropagation(rbacMuxer, logger);
                logger?.LogInformation("Redis connected using RBAC/Entra ID authentication with runtime service principal (auto token refresh enabled).");
                return new CacheConnectionManager(rbacMuxer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to Redis via RBAC. RBAC error: {ex.Message}" +
                    (hasPassword ? " (key-based auth was also attempted and failed earlier.)" : string.Empty), ex);
            }
        }

        private static async Task<ConnectionMultiplexer> ConnectWithRbacAsync(string connectionString, string tenantId, string clientId, string clientSecret)
        {
            // Start from the original connection string so we keep the host:port (6380 for classic Azure Cache
            // for Redis, 10000 for Azure Managed Redis) plus any operational options the caller chose
            // (abortConnect, clientName, etc.). Then clear the credential fields and switch to AAD auth.
            var options = ConfigurationOptions.Parse(connectionString);

            // Strip any stale key-based credentials parsed out of the connection string — Microsoft.Azure.StackExchangeRedis
            // will populate User (object id) and Password (bearer token) itself and keep them fresh.
            options.Password = null;
            options.User = null;

            options.Ssl = true;
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 15000;
            options.SyncTimeout = 15000;
            options.AsyncTimeout = 15000;

            // RESP3 bundles the interactive and pub/sub pipes on a single connection so both get re-authenticated
            // on token refresh. With RESP2 the subscription pipe is closed on each token expiry (then restored),
            // which surfaces as MicrosoftEntraTokenExpired errors on the cache metrics. Both classic Redis 6.0
            // and Azure Managed Redis (Enterprise 7.x) support RESP3.
            options.Protocol = RedisProtocol.Resp3;

            // Acquires the initial token and registers the proactive refresh + re-auth-on-reconnect handlers.
            await options.ConfigureForAzureWithServicePrincipalAsync(clientId, tenantId, clientSecret).ConfigureAwait(false);

            return await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
        }

        private static bool HasLoopbackEndpoint(ConfigurationOptions options)
        {
            foreach (var endpoint in options.EndPoints)
            {
                if (endpoint is DnsEndPoint dnsEndpoint &&
                    (string.Equals(dnsEndpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(dnsEndpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
                     string.Equals(dnsEndpoint.Host, "::1", StringComparison.Ordinal)))
                {
                    return true;
                }

                if (endpoint is IPEndPoint ipEndpoint && IPAddress.IsLoopback(ipEndpoint.Address))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Pings Redis after RBAC connection with a small retry budget to absorb the typical
        /// data-plane propagation delay after a fresh <c>accessPolicyAssignment</c> create
        /// (the installer might have created the assignment seconds before this call).
        /// </summary>
        private static void PingWithRetryForPolicyPropagation(ConnectionMultiplexer muxer, ILogger logger)
        {
            const int maxAttempts = 4;
            var db = muxer.GetDatabase();
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    db.Ping();
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts)
                {
                    var waitMs = 5000 * attempt;
                    logger?.LogWarning($"Redis RBAC ping failed (attempt {attempt} of {maxAttempts}): {ex.Message}. Waiting {waitMs / 1000}s for access policy propagation and retrying...");
                    Task.Delay(waitMs).GetAwaiter().GetResult();
                }
            }
        }

        /// <summary>
        /// Resets the singleton so the next call to GetConnectionManager will create a new connection.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _connectionManager?._muxer?.Dispose();
                _connectionManager = null;
            }
        }
        #endregion

        public IDatabase GetDatabase()
        {
            return _conn;
        }
        public ConnectionMultiplexer ConnectionMultiplexer
        {
            get { return _muxer; }
        }

        public async Task<string> GetString(string key)
        {
            var results = await GetDatabase().StringGetAsync(new RedisKey(key));

            if (results.HasValue)
            {
                return results.ToString();
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Get a string that's been temporarily cached & indentified by input params.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task<string> GetStringCache(object input)
        {
            return await GetStringCache(JsonConvert.SerializeObject(input));
        }
        /// <summary>
        /// Get a string that's been temporarily cached & indentified by input params.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task<string> GetStringCache(string inputString)
        {
            var results = await GetDatabase().StringGetAsync(new RedisKey(GetStringCacheKeyName(inputString)));

            if (results.HasValue)
            {
                return results.ToString();
            }
            else
            {
                return null;
            }
        }
        /// <summary>
        /// Set a string value that's been temporarily cached & indentified by input params. Expires in 24 hours.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task CacheStringOneDay(string inputString, string responseToCache)
        {
            var db = GetDatabase();
            var key = new RedisKey(GetStringCacheKeyName(inputString));
            await db.StringSetAsync(key, new RedisValue(responseToCache));

            await db.KeyExpireAsync(key, DateTime.Now.AddDays(1));
        }

        /// <summary>
        /// Set a string value that's been temporarily cached & indentified by input params. Expires in 24 hours.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task CacheStringOneDay(string inputString)
        {
            var db = GetDatabase();
            var key = new RedisKey(GetStringCacheKeyName(inputString));
            await db.StringSetAsync(key, new RedisValue(inputString));

            await db.KeyExpireAsync(key, DateTime.Now.AddDays(1));
        }



        string GetStringCacheKeyName(string inputString)
        {
            return "string_cache:" + inputString.GetHashCode();
        }

        public async Task SetString(string key, string val)
        {
            await GetDatabase().StringSetAsync(new RedisKey(key), new RedisValue(val));
        }

        public async Task DeleteString(string key)
        {
            await GetDatabase().KeyDeleteAsync(key);
        }

    }
}