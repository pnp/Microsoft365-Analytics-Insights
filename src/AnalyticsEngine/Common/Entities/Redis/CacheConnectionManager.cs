using Azure.Identity;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Common.Entities.Redis
{
    /// <summary>
    /// Wrapper class for redis connection. Tries key-based auth first, falls back to RBAC (Entra ID) if keys are disabled.
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
                var muxer = ConnectionMultiplexer.Connect(connectionString);
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
                var hostname = ExtractHostname(connectionString);
                var muxer = ConnectWithRbac(hostname, tenantId, clientId, clientSecret);
                var db = muxer.GetDatabase();
                db.Ping();
                logger?.LogInformation("Redis connected using RBAC/Entra ID authentication with runtime account.");
                return new CacheConnectionManager(muxer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to Redis with both key-based and RBAC auth. RBAC error: {ex.Message}", ex);
            }
        }

        private static string ExtractHostname(string connectionString)
        {
            // Connection string format: "host:port,password=...,ssl=True,abortConnect=False"
            // or just "host:port"
            if (string.IsNullOrEmpty(connectionString))
                throw new ArgumentException("Redis connection string is null or empty.");

            var parts = connectionString.Split(',');
            var hostPort = parts[0].Trim();
            // Remove port if present
            var colonIdx = hostPort.IndexOf(':');
            return colonIdx > 0 ? hostPort.Substring(0, colonIdx) : hostPort;
        }

        private static ConnectionMultiplexer ConnectWithRbac(string hostname, string tenantId, string clientId, string clientSecret)
        {
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var token = credential.GetToken(
                new Azure.Core.TokenRequestContext(new[] { "https://redis.azure.com/.default" }));

            // Extract the OID (Object ID) from the token for the username
            string username = null;
            var tokenParts = token.Token.Split('.');
            if (tokenParts.Length >= 2)
            {
                try
                {
                    var payload = tokenParts[1];
                    // Pad base64 if needed
                    payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                    var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload));
                    var claims = JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(json);
                    if (claims.ContainsKey("oid"))
                        username = claims["oid"].ToString();
                }
                catch
                {
                    // If token parsing fails, leave username null
                }
            }

            var options = new ConfigurationOptions
            {
                EndPoints = { { hostname, 6380 } },
                Ssl = true,
                AbortOnConnectFail = false,
                Password = token.Token,
                User = username
            };

            return ConnectionMultiplexer.Connect(options);
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
