using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Common.Entities.Redis
{
    /// <summary>
    /// Wrapper class for redis connection. Tries key-based auth first, falls back to RBAC (Entra ID) if keys are disabled.
    /// On the RBAC path the connection uses <see cref="Microsoft.Azure.StackExchangeRedis"/> which proactively refreshes
    /// the Entra access token before it expires and re-authenticates the multiplexer on reconnect — without this the
    /// multiplexer would silently start failing all commands ~60–90 min after process start with
    /// <c>MicrosoftEntraAuthenticationFailure</c> on the server and <c>SocketClosed</c> / <c>RedisTimeoutException</c>
    /// on the client.
    /// </summary>
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
        /// Gets or creates a singleton connection manager. Tries key-based connection string first;
        /// if that fails (e.g. keys disabled by policy), falls back to RBAC/Entra ID token auth using the runtime account.
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
            // Try key-based auth first
            try
            {
                var keyOptions = ConfigurationOptions.Parse(connectionString);
                keyOptions.ConnectTimeout = 15000;
                keyOptions.SyncTimeout = 15000;
                keyOptions.AsyncTimeout = 15000;
                var muxer = ConnectionMultiplexer.Connect(keyOptions);
                // Test the connection with a ping
                var db = muxer.GetDatabase();
                db.Ping();
                logger?.LogInformation("Redis connected using key-based authentication.");
                return new CacheConnectionManager(muxer);
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Redis key-based auth failed ({ex.Message}). Attempting RBAC/Entra ID auth...");
            }

            // Fall back to RBAC (Entra ID) token auth using the runtime account
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new InvalidOperationException(
                    "Redis key-based auth failed and RBAC fallback cannot proceed: runtime account credentials (tenantId, clientId, clientSecret) are not configured.");
            }

            try
            {
                // Microsoft.Azure.StackExchangeRedis ConfigureForAzure* methods are async (they acquire the
                // initial bearer token and register handlers to refresh it before expiry). We're inside a sync
                // singleton-init path that runs once per process under _lock — wrap in Task.Run to detach from
                // any ASP.NET / OWIN sync context and avoid the classic sync-over-async deadlock.
                var muxer = Task.Run(() => ConnectWithRbacAsync(connectionString, tenantId, clientId, clientSecret)).GetAwaiter().GetResult();
                var db = muxer.GetDatabase();
                db.Ping();
                logger?.LogInformation("Redis connected using RBAC/Entra ID authentication with runtime account (auto token refresh enabled).");
                return new CacheConnectionManager(muxer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to Redis with both key-based and RBAC auth. RBAC error: {ex.Message}", ex);
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
