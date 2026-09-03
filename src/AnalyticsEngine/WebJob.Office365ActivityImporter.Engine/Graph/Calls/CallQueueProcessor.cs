using Azure.Identity; // Added for ClientSecretCredential
using Azure.Messaging.ServiceBus;
using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Models;
using DataUtils;
using DataUtils.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Calls
{
    /// <summary>
    /// Processes calls from ServiceBus queue, put there from the "new call" endpoint
    /// </summary>
    public class CallQueueProcessor : IDisposable
    {
        #region Constructors

        public event EventHandler CallProcessed;

        private ServiceBusClient _sbClient;
        private ServiceBusProcessor _processor;
        private ILogger _logger;
        private ImportAppIndentityOAuthContext _auth;
        private string _thisTenantId = null;
        private CallRecordImporter _callRecordImporter;
        private bool _isInitialised = false;

        public ServiceBusClient ServiceBusClient => _sbClient;

        /// <summary>
        /// Creates a processor for the calls queue. The caller owns the instance and must keep it for
        /// the lifetime of the process: the Service Bus listener has to survive across import cycles.
        /// (This replaces a process-wide static singleton - see issue #378.)
        /// </summary>
        public CallQueueProcessor(AppConfig config, string thisTenantId)
        {
            // Use seperate telemetry context from rest of the importer
            _logger = new AnalyticsLogger(config.AppInsightsConnectionString, "Office365CallsImporter");

            _auth = new GraphAppIndentityOAuthContext(_logger, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);
            this._thisTenantId = thisTenantId;

            // Always authenticate to Service Bus with Entra ID RBAC (the runtime service principal) -
            // never a SAS key. The namespace + queue are still read from the existing ServiceBus
            // connection string's Endpoint, so existing installs keep their current config; the shared
            // access key in it is ignored. The runtime SP needs the "Azure Service Bus Data Owner" role
            // on the namespace (assigned by the installer). See issue #138.
            _logger.LogInformation("Initializing ServiceBusClient using Entra ID RBAC (no SAS keys).");
            var sbProps = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString);
            _sbClient = CreateRbacServiceBusClient(config);
            _processor = _sbClient.CreateProcessor(sbProps.EntityPath, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 10,
                PrefetchCount = 0,
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                MaxAutoLockRenewalDuration = TimeSpan.FromHours(24),        // Queue should be configured for 5 minute lock timeout
                AutoCompleteMessages = false                                // Messages are completed only when the migrator has succeeded to migrate the file
            });
        }

        #endregion

        /// <summary>
        /// Builds a <see cref="ServiceBusClient"/> that authenticates with Entra ID RBAC (the runtime
        /// service principal via <see cref="ClientSecretCredential"/>) - never a SAS key. The fully
        /// qualified namespace is read from the configured Service Bus connection string's Endpoint; the
        /// shared access key in that string is ignored. The runtime service principal needs the
        /// "Azure Service Bus Data Owner" role on the namespace (assigned by the installer). See issue #138.
        /// </summary>
        public static ServiceBusClient CreateRbacServiceBusClient(AppConfig config)
        {
            var sbProps = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString);
            var credential = new ClientSecretCredential(config.TenantGUID.ToString(), config.ClientID, config.ClientSecret);
            return new ServiceBusClient(sbProps.FullyQualifiedNamespace, credential);
        }

        public async Task Init()
        {
            await Init(null);
        }

        public async Task Init(ManualGraphCallClient manualGraphCallClient)
        {
            if (_isInitialised)
            {
                return;
            }
            await _auth.InitClientCredential();
            var graphClient = GraphServiceClientFactory.CreateWithTimeout(_auth.Creds, TimeSpan.FromHours(1));

            var teamsLoadContext = new TeamsLoadContext(graphClient);

            // Use manual graph call client if provided (for testing), or create a new one if not
            var graphCallClient = manualGraphCallClient ?? new ManualGraphCallClient(_auth, _logger);

            _callRecordImporter = new CallRecordImporter(
                new GraphCallRecordSourceLoader(graphCallClient, teamsLoadContext, _logger, _thisTenantId),
                new SqlCallRecordPersistenceManager(_logger),
                _logger);

            _isInitialised = true;
        }

        public async Task BeginProcessCallsQueue()
        {
            if (!_isInitialised)
            {
                throw new InvalidOperationException("CallQueueProcessor not initialised. Call Init() first.");
            }

            if (_processor.IsProcessing)
            {
                return;
            }

            // Add handler to process messages
            _processor.ProcessMessageAsync += ProcessSBMessagesAsync;

            // Add handler to process any errors
            _processor.ProcessErrorAsync += ExceptionReceivedHandler;

            // Start processing
            await _processor.StartProcessingAsync();
            if (_processor.IsProcessing)
            {
                _logger.LogInformation("ServiceBus client: Now listening for service-bus messages.");
            }
            else
            {
                _logger.LogWarning("ServiceBus client: Not listening for service-bus messages?");
            }
        }

        public static async Task AddChangeMsgToQueue(List<GraphChangeNotification> changes, ILogger logger, ServiceBusSender sbSender)
        {
            await AddChangeMsgToQueue(changes, logger, new ServiceBusCallNotificationQueueSender(sbSender));
        }

        /// <summary>
        /// Queue each notification for processing. Takes the queue as a port so the dispatch can be
        /// tested without Service Bus. See issue #378.
        /// </summary>
        public static async Task AddChangeMsgToQueue(List<GraphChangeNotification> changes, ILogger logger, ICallNotificationQueueSender queue)
        {
            foreach (var change in changes)
            {
                string callId = change.ResourceData.Id;

                if (!string.IsNullOrEmpty(callId))
                {
                    logger.LogInformation($"New call POSTed from Graph with ID '{callId}'");
                }
                else
                {
                    logger.LogInformation($"New call POSTed from Graph with unknown ID. Adding to service-bus queue anyway.");
                }

                var json = JsonConvert.SerializeObject(change);
                await queue.SendAsync(json);
            }
        }

        /// <summary>
        /// Process new message in Service Bus queue. Called automatically.
        /// </summary>
        async Task ProcessSBMessagesAsync(ProcessMessageEventArgs args)
        {
            string msgBody = args.Message.Body.ToString();

            _logger.LogInformation($"New message received from ServiceBus with ID '{args.Message.MessageId}'.");

            GraphChangeNotification change = null;
            try
            {
                change = JsonConvert.DeserializeObject<GraphChangeNotification>(msgBody);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error deserialising body '{msgBody}' from ServiceBus. Exception: {ex.Message}");
            }
            if (change == null)
            {
                // Unexpected message contents. Deadletter.
                await args.DeadLetterMessageAsync(args.Message);
                return;
            }

            bool success = false;
            if (change != null)
            {
                try
                {
                    // Process change
                    success = await ProcessGraphChange(change);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"ServiceBus processing error: processing call notification with msg body '{msgBody}'. Exception: {ex.Message}");
                }
            }
            if (success)
            {
                // Complete the message. messages is deleted from the queue. 
                try
                {
                    await args.CompleteMessageAsync(args.Message);
                    _logger.LogInformation($"Succesfully processed & completed ServiceBus message ID '{args.Message.MessageId}'");
                }
                catch (ServiceBusException ex)
                {
                    _logger.LogError(ex, $"Couldn't complete ServiceBus message '{args.Message.MessageId}': " + ex.Message);
#if DEBUG
                    throw;
#endif
                }
            }
            else
            {
                // Leave for processing later
                _logger.LogInformation($"Abandoning ServiceBus message ID '{args.Message.MessageId}' as import was NOT succesful");
                await args.AbandonMessageAsync(args.Message);
            }
        }

        async Task<bool> ProcessGraphChange(GraphChangeNotification graphChangeNotification)
        {
            var call = await _callRecordImporter.ImportFromNotification(graphChangeNotification);

            if (call != null)
            {
                CallProcessed?.Invoke(this, EventArgs.Empty);

                _logger.LogInformation($"Added call ID '{call.GraphCallID}' to database from ServiceBus.");
                return true;
            }
            else
            {
                return false;
            }
        }


        Task ExceptionReceivedHandler(ProcessErrorEventArgs args)
        {
            _logger.LogError(args.Exception, args.Exception.Message);
            // Log the full exception (type + message + stack) as text too: the AppInsights ILogger
            // adapter formats only the message and drops the Exception argument above, so otherwise the
            // stack never reaches the trace logs (why a bare 401 showed up with "no exception logged").
            _logger.LogError($"ServiceBus processor encountered an exception: {args.Exception}");

            _logger.LogError("Exception context for troubleshooting:");
            _logger.LogError($"- Namespace: {args.FullyQualifiedNamespace}");
            _logger.LogError($"- Entity Path: {args.EntityPath}");
            _logger.LogError($"- Error Source: {Enum.GetName(typeof(ServiceBusErrorSource), args.ErrorSource)}");
            return Task.CompletedTask;
        }

        #region Dispose

        public void Dispose()
        {
            // Sync-over-async in Dispose is acceptable here: this WebJob doesn't run inside an
            // ASP.NET request context, so there's no captured SynchronizationContext to deadlock on.
            // The previous fire-and-forget pattern dropped the returned ValueTask, so the host
            // could exit while ServiceBus messages were still being drained and any teardown
            // exception was silently swallowed.
            try
            {
                DisposeAsyncCore().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Error during {nameof(CallQueueProcessor)} dispose: {ex.Message}");
            }
            GC.SuppressFinalize(this);
        }


        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_processor != null)
            {
                if (_processor.IsProcessing)
                {
                    _logger.LogInformation("ServiceBus client: Service-bus processor stopped.");
                    await _processor.StopProcessingAsync();
                }

                // Unsubscribe handlers BEFORE disposing the processor (after disposal the
                // event accessors can throw ObjectDisposedException) and before nulling the field.
                _processor.ProcessMessageAsync -= ProcessSBMessagesAsync;
                _processor.ProcessErrorAsync -= ExceptionReceivedHandler;

                await _processor.DisposeAsync().ConfigureAwait(false);

                _processor = null;
            }
        }

        #endregion
    }
}
