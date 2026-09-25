using Newtonsoft.Json;
using System;
using System.Globalization;

namespace App.ControlPanel.Engine.Entities
{
    public class InstallerProxyConfig
    {
        // Legacy JSON names preserve compatibility with existing proxyconfig.dat files.
        [JsonProperty("UseFtpProxy")]
        public bool UseProxy { get; set; }

        [JsonProperty("ProxyHost")]
        public string Host { get; set; }

        [JsonProperty("ProxyPort")]
        public int Port { get; set; }

        [JsonProperty("IntegratedAuth")]
        public bool IntegratedAuth { get; set; }

        [JsonProperty("ProxyUsername")]
        public string Username { get; set; }

        [JsonProperty("ProxyPassword")]
        public string Password { get; set; }

        [JsonIgnore]
        public bool IsValid => ValidationError == null;

        /// <summary>
        /// Why this configuration cannot be used, worded for the admin who typed it; <c>null</c> when it is
        /// valid (or no proxy is used).
        /// </summary>
        [JsonIgnore]
        public string ValidationError
        {
            get
            {
                if (!UseProxy) return null;
                if (!TryNormalise(Host, Port, out _, out _, out var error)) return error;
                if (!IntegratedAuth && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)))
                {
                    return "Enter the proxy user name and password, or use integrated authentication.";
                }
                return null;
            }
        }

        /// <summary>
        /// The proxy's address, always built as <c>http://host:port/</c> from the normalised host and port.
        /// </summary>
        /// <remarks>
        /// Deliberately not <c>new WebProxy(Host, Port)</c>. On .NET Framework that constructor builds
        /// <c>new Uri("http://" + Host + ":" + Port)</c>, so a host typed with its scheme -
        /// <c>http://proxy.contoso.com</c> - became <c>http://http//proxy.contoso.com:8080</c>, a proxy
        /// whose host name is the literal string <c>http</c>, and every request failed with
        /// "The remote name could not be resolved: 'http'" (#613). .NET Core / .NET 5+ normalise the same
        /// input, so this will not reproduce on the net10 branch or under pwsh.
        /// The scheme is always <c>http</c> because that is how a client addresses a proxy; HTTPS targets are
        /// tunnelled through it with CONNECT.
        /// </remarks>
        public bool TryGetProxyAddress(out Uri address, out string error)
        {
            address = null;
            if (!TryNormalise(Host, Port, out var host, out var port, out error)) return false;

            try
            {
                address = new UriBuilder(Uri.UriSchemeHttp, host, port).Uri;
                return true;
            }
            catch (UriFormatException)
            {
                error = $"'{host}' is not a valid proxy server name.";
                return false;
            }
        }

        /// <summary>
        /// Reduces what an admin typed into the host and port boxes to a bare host name and a port:
        /// strips an <c>http://</c> / <c>https://</c> scheme and trailing slashes, and accepts a
        /// <c>host:port</c> as long as it does not contradict the port box.
        /// </summary>
        public static bool TryNormalise(string host, int port, out string normalisedHost, out int effectivePort, out string error)
        {
            normalisedHost = null;
            effectivePort = 0;
            error = null;

            var value = host?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                error = "Enter the proxy server name, for example proxy.contoso.com.";
                return false;
            }

            foreach (var scheme in new[] { "http://", "https://" })
            {
                if (value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                {
                    value = value.Substring(scheme.Length);
                    break;
                }
            }
            value = value.TrimEnd('/');

            if (value.Length == 0 || value.IndexOfAny(new[] { '/', '\\', '?', '#', '@', ' ', '\t' }) >= 0)
            {
                error = "Enter only the proxy server name, for example proxy.contoso.com - without a path or user name. " +
                    "Put the port number in the Port box.";
                return false;
            }

            var embeddedPort = 0;
            string portText = null;
            if (value.StartsWith("[", StringComparison.Ordinal))
            {
                // Bracketed IPv6 literal, e.g. [fd00::10] or [fd00::10]:3128 - WebProxy has always accepted these.
                var close = value.IndexOf(']');
                if (close < 0 || (close + 1 < value.Length && value[close + 1] != ':'))
                {
                    error = $"'{host.Trim()}' is not a valid proxy server name.";
                    return false;
                }
                if (close + 1 < value.Length) portText = value.Substring(close + 2);
                value = value.Substring(0, close + 1);
            }
            else
            {
                var colon = value.IndexOf(':');
                if (colon >= 0)
                {
                    if (colon == 0 || value.IndexOf(':', colon + 1) >= 0)
                    {
                        error = $"'{host.Trim()}' is not a valid proxy server name. Enter only the server name, for example " +
                            "proxy.contoso.com, and put the port number in the Port box.";
                        return false;
                    }
                    portText = value.Substring(colon + 1);
                    value = value.Substring(0, colon);
                }
            }

            if (portText != null &&
                (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out embeddedPort) ||
                 embeddedPort < 1 || embeddedPort > 65535))
            {
                error = $"'{host.Trim()}' does not end with a valid port number. Enter only the server name, for example " +
                    "proxy.contoso.com, and put the port number in the Port box.";
                return false;
            }

            if (embeddedPort > 0 && port > 0 && port != embeddedPort)
            {
                error = $"The proxy host ends with port {embeddedPort}, but the Port box says {port}. " +
                    "Remove the port from the host, or make the two match.";
                return false;
            }

            effectivePort = embeddedPort > 0 ? embeddedPort : port;
            if (effectivePort < 1 || effectivePort > 65535)
            {
                error = "Enter the proxy port number, for example 8080.";
                effectivePort = 0;
                return false;
            }

            normalisedHost = value;
            return true;
        }

        [JsonIgnore]
        public static InstallerProxyConfig Default => new InstallerProxyConfig();
    }
}
