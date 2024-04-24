using App.ControlPanel.Engine.Entities;
using FluentFTP;
using FluentFTP.Proxy.AsyncProxy;
using System;
using System.Net;

namespace App.ControlPanel.Engine
{
    public class FtpClientFactory
    {
        public static AsyncFtpClient GetFtpClient(string ftpAddress, string ftpUsername, string ftpPassword, InstallerFtpConfig ftpConfig)
        {
            if (ftpConfig is null)
            {
                throw new ArgumentNullException(nameof(ftpConfig));
            }

            const int FTPS_PORT = 990;

            AsyncFtpClient client = null;
            if (ftpConfig.UseFtpProxy)
            {
                var proxyProfile = new FtpProxyProfile
                {
                    ProxyHost = ftpConfig.ProxyHost,
                    ProxyPort = ftpConfig.ProxyPort,
                    FtpPort = FTPS_PORT,
                    FtpHost = ftpAddress
                };
                if (ftpConfig.IntegratedAuth)
                {
                    proxyProfile.ProxyCredentials = (NetworkCredential)CredentialCache.DefaultCredentials;
                }
                else
                {
                    proxyProfile.ProxyCredentials = new NetworkCredential(ftpConfig.ProxyUsername, ftpConfig.ProxyPassword);
                }
                client = new AsyncFtpClientHttp11Proxy(proxyProfile)
                {
                    Host = ftpAddress
                };

                client.Host = ftpAddress;
            }
            else
            {
                client = new AsyncFtpClient(ftpAddress);
            }


            client.Port = FTPS_PORT;
            client.Credentials = new NetworkCredential(ftpUsername, ftpPassword);

            // https://github.com/robinrodricks/FluentFTP/issues/466

            client.Config.DownloadDataType = FtpDataType.Binary;
            client.Config.RetryAttempts = 20;
            client.Config.SocketPollInterval = 1000;
            client.Config.EncryptionMode = FtpEncryptionMode.Implicit;

            return client;
        }

    }
}
