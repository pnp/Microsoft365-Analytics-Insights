using Azure.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Net.Http;

namespace WebJob.Office365ActivityImporter.Engine
{
    public class ExceptionHandler
    {
        [Obsolete]
        public static bool IsThrottlingError(HttpRequestException ex, HttpResponseMessage response, ILogger telemetry)
        {
            if (response == null)
            {
                return false;
            }
            Console.WriteLine($"Got ${nameof(HttpRequestException)}. Error = " + ex.Message);
            if (ex.InnerException != null) telemetry.LogError(ex, "Inner exception = " + ex.InnerException.Message);

            if (response != null)
            {
                string errBody = string.Empty;
                try
                {
                    errBody = response.Content.ReadAsStringAsync().Result;
                    telemetry.LogError("Error Body = " + errBody);
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }

                if (errBody.Contains("Too many requests"))
                {
                    return true;
                }
            }

            return false;
        }

        [Obsolete]
        public static bool IsTokenExpiredError(HttpRequestException ex, HttpResponseMessage response, AccessToken token)
        {
            if (response == null)
            {
                return false;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (token.ExpiresOn < DateTime.UtcNow)
                {
                    return true;
                }
                else
                {
                    return false;
                }
            }

            return false;
        }

        internal static void OutputInnerExceptionIfThereIsOne(Exception ex)
        {
            if (ex.InnerException != null)
            {
                OutputInnerExceptionIfThereIsOne(ex.InnerException);
            }
            else
            {

                Console.WriteLine($"ERROR: Got exception type '{ex.GetType().Name}': {ex.Message}");
            }
        }
    }
}
