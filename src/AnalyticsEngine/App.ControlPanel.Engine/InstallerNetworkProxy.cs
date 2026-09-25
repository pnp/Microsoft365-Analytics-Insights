using App.ControlPanel.Engine.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Applies the installer's proxy preference process-wide, so every HTTP client the installer - and the
    /// Azure SDK it uses - creates goes through it, with its credentials.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>.NET 10 port.</b> The .NET Framework build sets <see cref="WebRequest.DefaultWebProxy"/>. There that
    /// is also what <c>HttpClient</c> uses, and it is read afresh for every request, so a change made in
    /// Proxy Configuration applies to the next request of every client already in use. Neither half holds
    /// on .NET Core / .NET 5+: <c>HttpClient</c> uses <see cref="HttpClient.DefaultProxy"/>, a separate
    /// setting, and a <c>SocketsHttpHandler</c> reads it once - when it first sends - and keeps that object
    /// for its lifetime. Azure.Core's shared transport is one such handler for the whole process. Assigning
    /// a new <see cref="WebProxy"/> to the default on each change would reach only clients that had not
    /// sent anything yet, and the installer would silently keep using the old proxy for Azure.
    /// </para>
    /// <para>
    /// So one <see cref="ProcessProxySwitch"/> is installed as both <see cref="HttpClient.DefaultProxy"/> and
    /// <see cref="WebRequest.DefaultWebProxy"/> before anything can send (<see cref="EnsureProcessWideSwitch"/>,
    /// from Program.Main), and every later change retargets that switch. A handler asks its proxy for the
    /// address on every request, so a change applies to the next request of every client - no restart - as it
    /// does on the Framework build. With no installer proxy the switch answers with the original
    /// <see cref="HttpClient.DefaultProxy"/> object (the system or environment proxy), using the signed-in
    /// user's credentials as the Framework build does.
    /// </para>
    /// <para>
    /// As on the Framework build, two things are outside it: <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c>
    /// environment variables, which Azure.Core applies to its own handler explicitly and which the host process
    /// owns; and a handler given its own <c>Proxy</c> - the Kudu deployment client builds one per run from the
    /// same preference (<see cref="CreateWebProxy"/>).
    /// </para>
    /// </remarks>
    public static class InstallerNetworkProxy
    {
        private static readonly object SyncRoot = new object();
        private static ProcessProxySwitch _switch;
        private static string _lastLoggedState;

        /// <summary>
        /// Installs the process-wide switch, answering with the system proxy, if it is not installed yet. Call it
        /// before anything sends: a handler that sent before it was installed keeps the proxy it captured.
        /// </summary>
        public static void EnsureProcessWideSwitch()
        {
            lock (SyncRoot)
            {
                EnsureSwitch();
            }
        }

        public static void ApplyProcessWide(InstallerProxyConfig config, ILogger logger)
        {
            var effectiveConfig = config ?? InstallerProxyConfig.Default;

            lock (SyncRoot)
            {
                // Built before anything changes: an unusable configuration throws here and leaves the process
                // proxy exactly as it was.
                var custom = effectiveConfig.UseProxy ? CreateWebProxy(effectiveConfig) : null;
                var processSwitch = EnsureSwitch();

                string stateKey;
                string message;
                if (custom != null)
                {
                    processSwitch.UseCustom(custom);

                    var authMode = effectiveConfig.IntegratedAuth
                        ? "integrated"
                        : $"basic as {effectiveConfig.Username}";
                    stateKey = $"custom|{custom.Address}|{authMode}";
                    message = $"Installer HTTP proxy enabled: {custom.Address} ({authMode} authentication).";
                }
                else
                {
                    processSwitch.UseSystem();
                    stateKey = "system";
                    message = "Installer HTTP proxy disabled: using the system proxy with default credentials.";
                }

                if (logger != null && !string.Equals(_lastLoggedState, stateKey, StringComparison.Ordinal))
                {
                    logger.LogInformation(message);
                    _lastLoggedState = stateKey;
                }
            }
        }

        /// <summary>
        /// Applies the preference if it is usable; otherwise leaves the process proxy exactly as it was and
        /// reports why, in words the admin can act on.
        /// </summary>
        /// <remarks>
        /// For the installer UI, which applies the saved preference as soon as it opens and before every run.
        /// A preference saved by an older build was only checked for "host present, port above zero", so it can
        /// hold a value the stricter #613 validation now refuses (a path, a user name, a port that contradicts
        /// the Port box). <see cref="ApplyProcessWide"/> throws for those; thrown from the main form's Load
        /// event or a button handler, that left the installer half-initialised or stuck in its working state.
        /// </remarks>
        public static bool TryApplyProcessWide(InstallerProxyConfig config, ILogger logger, out string error)
        {
            error = (config ?? InstallerProxyConfig.Default).ValidationError;
            if (error != null)
            {
                logger?.LogWarning($"The saved installer proxy settings can't be used, so the proxy was not changed: {error}");
                return false;
            }

            ApplyProcessWide(config, logger);
            return true;
        }

        internal static WebProxy CreateWebProxy(InstallerProxyConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!config.UseProxy) return null;

            if (!config.TryGetProxyAddress(out var proxyAddress, out var error))
            {
                throw new InvalidOperationException($"The installer proxy configuration is not valid: {error}");
            }

            return new WebProxy(proxyAddress)
            {
                Credentials = config.IntegratedAuth
                    ? CredentialCache.DefaultCredentials
                    : new NetworkCredential(config.Username, config.Password)
            };
        }

        /// <summary>The original <see cref="HttpClient.DefaultProxy"/>, which answers while no installer proxy is set; null until the switch is installed.</summary>
        internal static IWebProxy OriginalDefaultProxy
        {
            get { lock (SyncRoot) { return _switch?.SystemProxy; } }
        }

        /// <summary>What the process resolves proxies through right now: the installer's proxy, or the original default.</summary>
        internal static IWebProxy CurrentProxy
        {
            get { lock (SyncRoot) { return _switch?.Current; } }
        }

        private static ProcessProxySwitch EnsureSwitch()
        {
            if (_switch != null) return _switch;

            // Never null: the setter refuses null, and the lazily built default is the environment proxy, the
            // system proxy or a no-op one.
            var processSwitch = new ProcessProxySwitch(HttpClient.DefaultProxy);
            HttpClient.DefaultProxy = processSwitch;

            // Nothing in the product sends through WebRequest or WebClient on .NET 10, but a library still can;
            // on .NET 10 that setting is separate from HttpClient's, so it gets the same switch.
            WebRequest.DefaultWebProxy = processSwitch;

            _switch = processSwitch;
            return processSwitch;
        }

        /// <summary>
        /// The one proxy object every handler in the process captures. It resolves against the current
        /// preference on every request, which is what lets a change apply without a restart.
        /// </summary>
        internal sealed class ProcessProxySwitch : IWebProxy
        {
            private readonly ProxySwitchCredentials _credentials;
            private volatile WebProxy _custom;
            private volatile ICredentials _systemCredentials;

            internal ProcessProxySwitch(IWebProxy systemProxy)
            {
                SystemProxy = systemProxy;
                _credentials = new ProxySwitchCredentials(this);
            }

            internal IWebProxy SystemProxy { get; }

            internal WebProxy Custom => _custom;

            internal IWebProxy Current => (IWebProxy)_custom ?? SystemProxy;

            internal ICredentials SystemCredentials => _systemCredentials ?? CredentialCache.DefaultCredentials;

            internal void UseCustom(WebProxy proxy) => _custom = proxy ?? throw new ArgumentNullException(nameof(proxy));

            internal void UseSystem() => _custom = null;

            /// <summary>
            /// A handler reads this once, when it first sends, so it is a stable object that asks the current
            /// preference on each use. Setting it replaces the credentials used with the system proxy only.
            /// </summary>
            public ICredentials Credentials
            {
                get => _credentials;
                set => _systemCredentials = ReferenceEquals(value, _credentials) ? null : value;
            }

            public Uri GetProxy(Uri destination) => Current?.GetProxy(destination);

            public bool IsBypassed(Uri host) => Current?.IsBypassed(host) ?? true;
        }

        private sealed class ProxySwitchCredentials : ICredentials
        {
            private readonly ProcessProxySwitch _owner;

            internal ProxySwitchCredentials(ProcessProxySwitch owner)
            {
                _owner = owner;
            }

            public NetworkCredential GetCredential(Uri uri, string authType)
            {
                // The installer proxy's own credentials; with the system proxy, the signed-in user's, so an
                // integrated-authentication corporate proxy does not answer 407.
                var custom = _owner.Custom;
                var credentials = custom != null ? custom.Credentials : _owner.SystemCredentials;
                return credentials?.GetCredential(uri, authType);
            }
        }
    }
}
