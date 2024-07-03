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
        private TeamsLoadContext _teamsLoadContext;
        private ILogger _telemetry;
        private ImportAppIndentityOAuthContext _auth;
        private string _thisTenantId = null;
        private ManualGraphCallClient _graphCallClient;
        private bool _isInitialised = false;


        public static CallQueueProcessor _singleton = null;
        public static async Task<CallQueueProcessor> GetCallQueueProcessor(AppConfig config, string thisTenantId, ManualGraphCallClient graphCallClient)
        {
            if (_singleton == null)
            {
                _singleton = new CallQueueProcessor(config, thisTenantId);
                await _singleton.Init(graphCallClient);
            }

            return _singleton;
        }

        private CallQueueProcessor(AppConfig config, string thisTenantId)
        {
            // Use seperate telemetry context from rest of the importer
            _telemetry = new AnalyticsLogger(config.AppInsightsConnectionString, "Office365CallsImporter");

            _auth = new GraphAppIndentityOAuthContext(_telemetry, config.ClientID, config.TenantGUID.ToString(), config.ClientSecret, config.KeyVaultUrl, config.UseClientCertificate);
            this._thisTenantId = thisTenantId;

            _sbClient = new ServiceBusClient(config.ConnectionStrings.ServiceBusConnectionString);
            var sbConnectionInfo = ServiceBusConnectionStringProperties.Parse(config.ConnectionStrings.ServiceBusConnectionString);
            _processor = _sbClient.CreateProcessor(sbConnectionInfo.EntityPath, new ServiceBusProcessorOptions
            {
                MaxConcurrentCalls = 10,
                PrefetchCount = 0,
                ReceiveMode = ServiceBusReceiveMode.PeekLock,
                MaxAutoLockRenewalDuration = TimeSpan.FromHours(24),        // Queue should be configured for 5 minute lock timeout
                AutoCompleteMessages = false                                // Messages are completed only when the migrator has succeeded to migrate the file
            });
        }

        #endregion

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
            var graphClient = new GraphServiceClient(_auth.Creds);
            graphClient.HttpProvider.OverallTimeout = TimeSpan.FromHours(1);

            _teamsLoadContext = new TeamsLoadContext(graphClient);

            // Use manual graph call client if provided (for testing), or create a new one if not
            if (manualGraphCallClient == null)
            {
                _graphCallClient = new ManualGraphCallClient(_auth, _telemetry);
            }
            else
            {
                _graphCallClient = manualGraphCallClient;
            }
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
                _telemetry.LogInformation("ServiceBus client: Now listening for service-bus messages.");
            }
            else
            {
                _telemetry.LogWarning("ServiceBus client: Not listening for service-bus messages?");
            }
        }

        public static async Task AddChangeMsgToQueue(List<GraphChangeNotification> changes, ILogger telemetry, ServiceBusSender sbSender)
        {
            foreach (var change in changes)
            {
                string callId = change.ResourceData.Id;

                if (!string.IsNullOrEmpty(callId))
                {
                    telemetry.LogInformation($"New call POSTed from Graph with ID '{callId}'");
                }
                else
                {
                    telemetry.LogInformation($"New call POSTed from Graph with unknown ID. Adding to service-bus queue anyway.");
                }

                var json = JsonConvert.SerializeObject(change);
                await sbSender.SendMessageAsync(new ServiceBusMessage(json));
            }
        }

        /// <summary>
        /// Process new message in Service Bus queue. Called automatically.
        /// </summary>
        async Task ProcessSBMessagesAsync(ProcessMessageEventArgs args)
        {
            string msgBody = args.Message.Body.ToString();

            _telemetry.LogInformation($"New message received from ServiceBus with ID '{args.Message.MessageId}'.");

            GraphChangeNotification change = null;
            try
            {
                change = JsonConvert.DeserializeObject<GraphChangeNotification>(msgBody);
            }
            catch (Exception ex)
            {
                _telemetry.LogError(ex, $"Error deserialising body '{msgBody}' from ServiceBus. Exception: {ex.Message}");
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
                    _telemetry.LogError(ex, $"ServiceBus processing error: processing call notification with msg body '{msgBody}'. Exception: {ex.Message}");
                }
            }
            if (success)
            {
                // Complete the message. messages is deleted from the queue. 
                try
                {
                    await args.CompleteMessageAsync(args.Message);
                    _telemetry.LogInformation($"Succesfully processed & completed ServiceBus message ID '{args.Message.MessageId}'");
                }
                catch (ServiceBusException ex)
                {
                    _telemetry.LogError(ex, $"Couldn't complete ServiceBus message '{args.Message.MessageId}': " + ex.Message);
#if DEBUG
                    throw;
#endif
                }
            }
            else
            {
                // Leave for processing later
                _telemetry.LogInformation($"Abandoning ServiceBus message ID '{args.Message.MessageId}' as import was NOT succesful");
                await args.AbandonMessageAsync(args.Message);
            }
        }

        async Task<bool> ProcessGraphChange(GraphChangeNotification graphChangeNotification)
        {
            var call = await LoadAndSaveCallRecordFromChangeNotification(graphChangeNotification);

            if (call != null)
            {
                CallProcessed?.Invoke(this, EventArgs.Empty);

                _telemetry.LogInformation($"Added call ID '{call.GraphCallID}' to database from ServiceBus.");
                return true;
            }
            else
            {
                return false;
            }
        }


        async Task<CallRecordDTO> LoadAndSaveCallRecordFromChangeNotification(GraphChangeNotification change)
        {
            string callId = change?.ResourceData.Id;
            if (string.IsNullOrEmpty(callId))
            {
                _telemetry.LogInformation("ServiceBus error: couldn't find call ID in JSon. Ignoring event.");
                return null;
            }

            CallRecordDTO callResponse = null;
            using (var db = new AnalyticsEntitiesContext())
            {
                callResponse = await CallRecordDTO.LoadFromGraphByID(callId, _graphCallClient, _teamsLoadContext, this._telemetry, this._thisTenantId);
                if (!string.IsNullOrEmpty(callResponse.OrganizerEmail))
                {
                    await callResponse.SaveOrReplaceCallRecord(new TeamsAndCallsDBLookupManager(db), _telemetry);
                }
            }

            return callResponse;
        }


        Task ExceptionReceivedHandler(ProcessErrorEventArgs args)
        {
            _telemetry.LogError(args.Exception, args.Exception.Message);
            _telemetry.LogError($"ServiceBus processor encountered an exception '{args.Exception.Message}'.");

            _telemetry.LogError("Exception context for troubleshooting:");
            _telemetry.LogError($"- Namespace: {args.FullyQualifiedNamespace}");
            _telemetry.LogError($"- Entity Path: {args.EntityPath}");
            _telemetry.LogError($"- Error Source: {Enum.GetName(typeof(ServiceBusErrorSource), args.ErrorSource)}");
            return Task.CompletedTask;
        }

        #region Dispose

        public void Dispose()
        {
            DisposeAsyncCore().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }


        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_processor != null)
            {
                if (_processor.IsProcessing)
                {
                    _telemetry.LogInformation("ServiceBus client: Service-bus processor stopped.");
                    await _processor.StopProcessingAsync();
                }

                await _processor.DisposeAsync().ConfigureAwait(false);
            }

            _processor.ProcessMessageAsync -= ProcessSBMessagesAsync;
            _processor.ProcessErrorAsync -= ExceptionReceivedHandler;
            _processor = null;
        }

        #endregion
    }
}
