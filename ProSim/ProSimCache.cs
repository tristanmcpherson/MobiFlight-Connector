using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using GraphQL.Client.Abstractions.Websocket;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;

namespace MobiFlight.ProSim
{
    public class ProSimCache : ProSimCacheInterface
    {
        public event EventHandler Closed;
        public event EventHandler Connected;
        public event EventHandler ConnectionLost;
        public event EventHandler<string> AircraftChanged;

        private readonly string _instanceId = Guid.NewGuid().ToString("N").Substring(0, 6);
        private static bool DetailedDebugLogEnabled => Properties.Settings.Default.ProSimDetailedDebugLog;
        private bool _connected = false;

        private readonly object _cacheLock = new object();
        private readonly object _refreshLock = new object();

        private readonly Dictionary<string, IDisposable> _subscriptions = new Dictionary<string, IDisposable>();

        // ProSim SDK object 
        private IGraphQLWebSocketClient _connection;
        
        // Heartbeat timer to keep WebSocket connection active
        private Timer _heartbeatTimer;

        // Cache of subscribed DataRefs
        private Dictionary<string, CachedDataRef> _subscribedDataRefs = new Dictionary<string, CachedDataRef>();
        private Dictionary<string, DataRefDescription> _dataRefDescriptions = new Dictionary<string, DataRefDescription>();

        // Queue for writes that need to wait for data definitions refresh
        private readonly ConcurrentQueue<(string datarefPath, object value)> _pendingWrites = new ConcurrentQueue<(string, object)>();
        
        // Flag to track if refresh is in progress
        private volatile bool _refreshInProgress = false;

        // Helper class to store cached values
        private class CachedDataRef
        {
            public string Path { get; set; }
            public object Value { get; set; }
            public DataRef DataRefObject { get; set; }
            public int UpdateInterval { get; set; }
            public DataRefDescription DataRefDescription { get; set; }
        }

        private void LogDetailed(string message, LogSeverity severity = LogSeverity.Debug)
        {
            if (DetailedDebugLogEnabled)
            {
                Log.Instance.log(message, severity);
            }
        }

        private static string DescribeValue(object value)
        {
            return value == null ? "<null>" : $"{value} ({value.GetType().Name})";
        }

        public bool Connect()
        {
            try
            {
                var host = !string.IsNullOrWhiteSpace(Properties.Settings.Default.ProSimHost)
                    ? Properties.Settings.Default.ProSimHost
                    : "localhost";

                var port = Properties.Settings.Default.ProSimPort;

                LogDetailed($"ProSimCache[{_instanceId}] Connect requested. Host={host}, Port={port}");

                _connection = new GraphQLHttpClient($"http://{host}:{port}/graphql", new NewtonsoftJsonSerializer());
                _connection.InitializeWebsocketConnection();

                _connection.WebsocketConnectionState.Subscribe(state =>
                {
                    if (state == GraphQLWebsocketConnectionState.Connected)
                    {
                        Log.Instance.log("Connected to ProSim GraphQL WebSocket!", LogSeverity.Debug);
                        LogDetailed($"ProSimCache[{_instanceId}] WebSocket connected.");
                        _connected = true;
                        
                        // Refresh data definitions on connection
                        RefreshDataDefinitionsAsync().ContinueWith(task =>
                        {
                            if (task.IsFaulted)
                            {
                                Log.Instance.log($"Failed to refresh data definitions: {task.Exception?.GetBaseException().Message}", LogSeverity.Error);
                            }
                        });
                        
                        // Start heartbeat timer to keep WebSocket active
                        StartHeartbeat();
                        
                        Connected?.Invoke(this, new EventArgs());
                    }
                    else if (state == GraphQLWebsocketConnectionState.Disconnected)
                    {
                        if (_connected)
                        {
                            _connected = false;
                            // Stop heartbeat timer
                            StopHeartbeat();
                            // Clear data definitions on disconnection
                            int clearedCount = 0;
                            lock (_cacheLock)
                            {
                                clearedCount = _dataRefDescriptions.Count;
                                _dataRefDescriptions.Clear();
                            }
                            LogDetailed($"ProSimCache[{_instanceId}] Disconnected. Cleared {clearedCount} dataref definitions.");
                            ConnectionLost?.Invoke(this, new EventArgs());
                        }
                    }
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Instance.log($"Failed to connect to ProSim: {ex.Message}", LogSeverity.Error);
                _connected = false;
                return false;
            }
        }

        public void Clear()
        {
            int clearedCount = 0;
            lock (_cacheLock)
            {
                clearedCount = _dataRefDescriptions.Count;
                _dataRefDescriptions = new Dictionary<string, DataRefDescription>();
            }
            LogDetailed($"ProSimCache[{_instanceId}] Clear called. Cleared {clearedCount} dataref definitions.");
        }

        private void StartHeartbeat()
        {
            StopHeartbeat(); // Ensure we don't have multiple timers
            
            _heartbeatTimer = new Timer(5000); // 5 seconds
            _heartbeatTimer.Elapsed += async (sender, e) =>
            {
                if (IsConnected() && _connection != null)
                {
                    try
                    {
                        // Send a lightweight introspection query to keep WebSocket active
                        await _connection.SendQueryAsync<object>(new GraphQL.GraphQLRequest
                        {
                            Query = "{ __typename }"
                        });
                    }
                    catch (Exception ex)
                    {
                        LogDetailed($"Heartbeat failed: {ex.Message}");
                    }
                }
            };
            _heartbeatTimer.Start();
            LogDetailed("Started WebSocket heartbeat timer");
        }

        private void StopHeartbeat()
        {
            if (_heartbeatTimer != null)
            {
                _heartbeatTimer.Stop();
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
                LogDetailed("Stopped WebSocket heartbeat timer");
            }
        }

        private void SubscribeToDataRef(string datarefPath)
        {
            // Skip if we already have a subscription for this dataref
            if (_subscriptions.ContainsKey(datarefPath))
                return;

            var subscription = @"
                subscription ($names: [String!]!) {
                  dataRefs(names: $names) {
                    name
                    value
                  }
                }";

            var variables = new
            {
                names = new[] { datarefPath }
            };

            var dataRefObservable = _connection.CreateSubscriptionStream<DataRefSubscriptionResult>(new GraphQL.GraphQLRequest
            {
                Query = subscription,
                Variables = variables
            });

            var disposable = dataRefObservable.Subscribe(response =>
            {
                if (response?.Data != null)
                {
                    var dataRef = response.Data.DataRefs;
                    UpdateCachedValue(dataRef.Name, dataRef.Value);
                }
            });

            _subscriptions[datarefPath] = disposable;

            LogDetailed($"Created subscription for dataref: {datarefPath}");
        }

        private void UpdateCachedValue(string datarefPath, object value)
        {
            lock (_cacheLock)
            {
                if (_subscribedDataRefs.TryGetValue(datarefPath, out var cachedRef))
                {
                    // Update existing cache entry
                    cachedRef.Value = value;
                }
                else
                {
                    // Create new cache entry
                    var newCachedRef = new CachedDataRef
                    {
                        Path = datarefPath,
                        Value = value,
                    };

                    // Set DataRefDescription if available
                    if (_dataRefDescriptions.TryGetValue(datarefPath, out var description))
                    {
                        newCachedRef.DataRefDescription = description;
                    }

                    _subscribedDataRefs[datarefPath] = newCachedRef;
                }
            }
        }

        private readonly Dictionary<string, (string method, string graphqlType)> mutationLookup = new Dictionary<string, (string, string)>
        {
            { "System.Int32", ("writeInt", "Int!") },
            { "System.Double", ("writeFloat", "Float!") },
            { "System.Boolean", ("writeBool", "Boolean!") }
        };

        private void WriteOutValue(string datarefPath, object value)
        {
            // Check if refresh is in progress
            if (_refreshInProgress)
            {
                // Queue the write for later processing
                _pendingWrites.Enqueue((datarefPath, value));
                LogDetailed($"ProSimCache[{_instanceId}] Queued write during refresh. Path={datarefPath}, Value={DescribeValue(value)}");
                return;
            }

            LogDetailed($"ProSimCache[{_instanceId}] Dispatching write. Path={datarefPath}, Value={DescribeValue(value)}");
            WriteOutValueInternal(datarefPath, value);
        }

        private void WriteOutValueInternal(string datarefPath, object value)
        {
            try
            {
                if (!IsConnected() || _connection == null)
                {
                    LogDetailed($"ProSimCache[{_instanceId}] Write skipped: not connected. Path={datarefPath}");
                    return;
                }

                if (!_dataRefDescriptions.TryGetValue(datarefPath, out var description))
                {
                    LogDetailed($"ProSimCache[{_instanceId}] Write skipped: unknown dataref. Path={datarefPath}");
                    return;
                }

                if (!description.CanWrite)
                {
                    LogDetailed($"ProSimCache[{_instanceId}] Write skipped: dataref not writable. Path={datarefPath}");
                    return;
                }

                if (!mutationLookup.TryGetValue(description.DataType, out var mutation))
                {
                    LogDetailed($"ProSimCache[{_instanceId}] Write skipped: unsupported data type '{description.DataType}'. Path={datarefPath}");
                    return;
                }

                var (method, graphqlType) = mutation;
                LogDetailed(
                    $"ProSimCache[{_instanceId}] Write mutation prepared. Path={datarefPath}, Method={method}, DataType={description.DataType}, Value={DescribeValue(value)}");

                Task.Run(async () => {
                    try
                    {
                        var query = $@"
mutation ($name: String!, $value: {graphqlType}) {{
	dataRef {{
		{method}(name: $name, value: $value)
	}}
}}";
                        await _connection.SendMutationAsync<object>(new GraphQL.GraphQLRequest
                        {
                            Query = query,
                            Variables = new { name = datarefPath, value }
                        });
                        LogDetailed($"ProSimCache[{_instanceId}] Write completed. Path={datarefPath}, Method={method}");
                    }
                    catch (Exception ex)
                    {
                        LogDetailed($"ProSimCache[{_instanceId}] Write failed. Path={datarefPath}, Method={method}, Error={ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                LogDetailed($"ProSimCache[{_instanceId}] WriteOutValueInternal failed. Path={datarefPath}, Error={ex.Message}");
            }
        }

        private async Task RefreshDataDefinitionsAsync()
        {
            lock (_refreshLock)
            {
                if (_refreshInProgress)
                {
                    LogDetailed($"ProSimCache[{_instanceId}] RefreshDataDefinitions skipped: refresh already in progress.");
                    return;
                }
                _refreshInProgress = true;
            }

            try
            {
                if (!IsConnected() || _connection == null)
                {
                    LogDetailed($"ProSimCache[{_instanceId}] RefreshDataDefinitions aborted: Connected={IsConnected()}, ConnectionNull={_connection == null}.");
                    return;
                }

                LogDetailed("Refreshing ProSim data definitions...");
                
                var dataRefDescriptions = await _connection.SendQueryAsync<DataRefData>(new GraphQL.GraphQLRequest
                {
                    Query = @"
                    {
                        dataRef {
                        dataRefDescriptions: list {
                    		    name
                    		    description
                    		    canRead
                    		    canWrite
                    		    dataType
                    		    dataUnit
                            __typename
                        }
                        __typename
                        }
                    }"
                });

                if (dataRefDescriptions?.Data?.DataRef?.DataRefDescriptions == null)
                {
                    LogDetailed($"ProSimCache[{_instanceId}] RefreshDataDefinitions returned no dataref descriptions.");
                    return;
                }

                var newDataRefDescriptions = dataRefDescriptions.Data.DataRef.DataRefDescriptions.ToDictionary(drd => drd.Name);

                lock (_cacheLock)
                {
                    _dataRefDescriptions = newDataRefDescriptions;
                }

                LogDetailed($"Refreshed {_dataRefDescriptions.Count} data definitions");

                ProcessPendingWrites();
            }
            catch
            {
                // Ignore all errors
            }
            finally
            {
                lock (_refreshLock)
                {
                    _refreshInProgress = false;
                }
            }
        }

        private void ProcessPendingWrites()
        {
            if (!IsConnected())
            {
                return;
            }

            var totalPending = _pendingWrites.Count;

            if (totalPending == 0)
            {
                return;
            }

            LogDetailed($"Processing {totalPending} pending writes after data definitions refresh");

            while (_pendingWrites.TryDequeue(out var pendingWrite))
            {
                try
                {
                    if (_dataRefDescriptions.Count == 0)
                    {
                        continue;
                    }

                    if (!_dataRefDescriptions.ContainsKey(pendingWrite.datarefPath))
                    {
                        continue;
                    }

                    WriteOutValueInternal(pendingWrite.datarefPath, pendingWrite.value);
                }
                catch
                {
                    // Ignore all errors
                }
            }
        }

        public bool Disconnect()
        {
            if (_connected)
            {                
                lock (_subscribedDataRefs)
                {
                    _subscribedDataRefs.Clear();
                }

                lock (_cacheLock)
                {
                    _dataRefDescriptions.Clear();
                }

                // Clear pending writes
                while (_pendingWrites.TryDequeue(out _)) { }

                lock (_refreshLock)
                {
                    _refreshInProgress = false;
                }

                // Stop heartbeat timer
                StopHeartbeat();

                foreach (var subscription in _subscriptions.Values)
                {
                    subscription.Dispose();
                }

                _connected = false;
                _connection = null;
                Closed?.Invoke(this, new EventArgs());
            }
            return !_connected;
        }

        public bool IsConnected()
        {
            return _connected && _connection != null;
        }

        public object readDataref(string datarefPath)
        {
            if (!IsConnected())
            {
                return (double)0;
            }

            try
            {
                if (!_subscriptions.ContainsKey(datarefPath))
                {
                    SubscribeToDataRef(datarefPath);
                    return (double)0;
                }

                lock (_subscribedDataRefs)
                {
                    if (!_subscribedDataRefs.ContainsKey(datarefPath))
                    {
                        // Wait for data to be returned by the subscription
                        return (double)0;
                    }

                    // Cache the dictionary value to avoid redundant lookups
                    var subscribedDataRef = _subscribedDataRefs[datarefPath];
                    var value = subscribedDataRef.Value;

                    if (subscribedDataRef.DataRefDescription.DataType == "System.String")
                    {
                        return value;
                    }

                    var returnValue = (value == null) ? 0 : Convert.ToDouble(value);

                    return returnValue;
                }

            }
            catch (Exception ex)
            {
                Log.Instance.log($"Error reading dataref {datarefPath}: {ex.Message}", LogSeverity.Error);
                return (double)0;
            }
        }

        public void writeDataref(string datarefPath, object value)
        {
            if (!IsConnected())
            {
                LogDetailed($"ProSimCache[{_instanceId}] writeDataref skipped: not connected. Path={datarefPath}");
                return;
            }

            try
            {
                if (_dataRefDescriptions.Count == 0)
                {
                    if (_refreshInProgress)
                    {
                        _pendingWrites.Enqueue((datarefPath, value));
                        LogDetailed($"ProSimCache[{_instanceId}] writeDataref queued while refresh in progress. Path={datarefPath}, Value={DescribeValue(value)}");
                        return;
                    }

                    _pendingWrites.Enqueue((datarefPath, value));
                    LogDetailed($"ProSimCache[{_instanceId}] writeDataref queued and triggering refresh. Path={datarefPath}, Value={DescribeValue(value)}");
                    RefreshDataDefinitionsAsync().ConfigureAwait(false);
                    return;
                }

                if (!_dataRefDescriptions.ContainsKey(datarefPath))
                {
                    LogDetailed($"ProSimCache[{_instanceId}] writeDataref skipped: dataref not found in definitions. Path={datarefPath}");
                    return;
                }
                
                var description = _dataRefDescriptions[datarefPath];

                if (!description.CanWrite)
                {
                    LogDetailed($"ProSimCache[{_instanceId}] writeDataref skipped: dataref is not writable. Path={datarefPath}");
                    return;
                }

                var transformedValue = value;
                var targetDataType = description.DataType;

                try
                {
                    if (targetDataType == "System.Int32")
                    {
                        transformedValue = Convert.ToInt32(value);
                    }
                    else if (targetDataType == "System.Double")
                    {
                        transformedValue = Convert.ToDouble(value);
                    }
                    else if (targetDataType == "System.Boolean")
                    {
                        transformedValue = Convert.ToBoolean(value);
                    }
                }
                catch (Exception ex)
                {
                    LogDetailed(
                        $"ProSimCache[{_instanceId}] writeDataref conversion failed. Path={datarefPath}, TargetType={targetDataType}, Value={DescribeValue(value)}, Error={ex.Message}");
                    return;
                }

                LogDetailed(
                    $"ProSimCache[{_instanceId}] writeDataref accepted. Path={datarefPath}, TargetType={targetDataType}, Value={DescribeValue(transformedValue)}");
                WriteOutValue(datarefPath, transformedValue);
            }
            catch (Exception ex)
            {
                LogDetailed($"ProSimCache[{_instanceId}] writeDataref failed. Path={datarefPath}, Error={ex.Message}");
            }
        }

        public Dictionary<string, DataRefDescription> GetDataRefDescriptions()
        {
            Dictionary<string, DataRefDescription> snapshot;
            int count;
            lock (_cacheLock)
            {
                count = _dataRefDescriptions.Count;
                snapshot = new Dictionary<string, DataRefDescription>(_dataRefDescriptions);
            }

            LogDetailed($"ProSimCache[{_instanceId}] GetDataRefDescriptions. Connected={_connected}, Refreshing={_refreshInProgress}, Count={count}");

            return snapshot;
        }
    }
} 
