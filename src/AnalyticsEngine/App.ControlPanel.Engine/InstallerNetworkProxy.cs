using App.ControlPanel.Engine.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Net;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// Applies the installer's proxy preference to .NET Framework HTTP clients that otherwise fall back to
    /// <see cref="WebRequest.DefaultWebProxy"/> without credentials.
    /// </summary>
    /// <remarks>
    /// Azure.Core's default transport also reaches this setting on .NET Framework when no
    /// <c>HTTP_PROXY</c> / <c>HTTPS_PROXY</c> environment proxy is configured; those environment variables
    /// are owned by the host process and can still take precedence over the installer preference.
    /// </remarks>
    public static class InstallerNetworkProxy
    {
        private static readonly object SyncRoot = new object();
        private static bool _capturedOriginalProxy;
        private static IWebProxy _originalDefaultProxy;
        private static string _lastLoggedState;

        public static void ApplyProcessWide(InstallerProxyConfig config, ILogger logger)
        {
            var effectiveConfig = config ?? InstallerProxyConfig.Default;

            lock (SyncRoot)
            {
                CaptureOriginalProxyIfNeeded();

                string stateKey;
                string message;
                if (effectiveConfig.UseProxy)
                {
                    var proxy = CreateWebProxy(effectiveConfig);
                    WebRequest.DefaultWebProxy = proxy;

                    var authMode = effectiveConfig.IntegratedAuth
                        ? "integrated"
                        : $"basic as {effectiveConfig.Username}";
                    stateKey = $"custom|{proxy.Address}|{authMode}";
                    message = $"Installer HTTP proxy enabled: {proxy.Address} ({authMode} authentication).";
                }
                else
                {
                    if (_originalDefaultProxy != null)
                    {
                        _originalDefaultProxy.Credentials = CredentialCache.DefaultCredentials;
                    }

                    WebRequest.DefaultWebProxy = _originalDefaultProxy;
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

        private static void CaptureOriginalProxyIfNeeded()
        {
            if (_capturedOriginalProxy) return;

            _originalDefaultProxy = WebRequest.DefaultWebProxy;
            _capturedOriginalProxy = true;
        }
    }
}
