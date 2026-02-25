using MobiFlight.ProSim;
using MobiFlight.UI.Panels.Settings.Device;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using WebSocketSharp;

namespace MobiFlight.UI.Panels.Config
{
    public partial class ProSimDataRefPanel : UserControl
    {
        public event EventHandler ModifyTabLink;
        private static bool DetailedDebugLogEnabled => Properties.Settings.Default.ProSimDetailedDebugLog;

        private const int DataRefRetryIntervalMs = 1000;
        private const int MaxDataRefRetryCount = 10;
        private readonly Timer _dataRefRetryTimer = new Timer();
        private int _dataRefRetryCount = 0;

        private Dictionary<string, DataRefDescription> _dataRefDescriptions;
        private List<DataRefDescription> _canReadDataRefDescriptions;
        private List<DataRefDescription> _canReadDataRefDescriptionsFiltered;
        private bool _isLoading = true;
        private bool _isOutputMode = true;

        private IExecutionManager _executionManager;

        private static void LogDetailed(string message, LogSeverity severity = LogSeverity.Debug)
        {
            if (DetailedDebugLogEnabled)
            {
                Log.Instance.log(message, severity);
            }
        }

        [Description("ProSim DataRef Path"), Category("Data")]
        public string Path
        {
            get => DatarefPathTextBox.Text;
            set => DatarefPathTextBox.Text = value;
        }

        [Description("ProSim Transform Group"), Category("Data")]
        public TransformOptionsGroup TransformOptionsGroup
        {
            get => transformOptionsGroup1;
            set => transformOptionsGroup1 = value;
        }

        public ProSimDataRefPanel()
        {
            InitializeComponent();
            dataGridView1.AutoGenerateColumns = false;
            dataGridView1.AllowUserToResizeRows = false;
            dataGridView1.DataBindingComplete += DataGridView1_DataBindingComplete;

            SetMode(_isOutputMode);

            _dataRefRetryTimer.Interval = DataRefRetryIntervalMs;
            _dataRefRetryTimer.Tick += DataRefRetryTimer_Tick;
            Disposed += (sender, args) =>
            {
                _dataRefRetryTimer.Stop();
                _dataRefRetryTimer.Dispose();
            };
            LogDetailed($"ProSimDataRefPanel initialized. OutputMode={_isOutputMode}");
        }

        private void DataGridView1_DataBindingComplete(object sender, DataGridViewBindingCompleteEventArgs e)
        {
            _isLoading = false;
            SelectRowForCurrentPath();
        }

        public void SetMode(bool isOutputPanel)
        {
            _isOutputMode = isOutputPanel;
            transformOptionsGroup1.setMode(isOutputPanel);
            LogDetailed($"ProSimDataRefPanel SetMode. OutputMode={_isOutputMode}");
        }

        public void Init(IExecutionManager executionManager)
        {
            _executionManager = executionManager;
            LogDetailed("ProSimDataRefPanel Init called.");
        }

        internal void syncToConfig(OutputConfigItem config)
        {
            config.Source = new Base.ProSimSource() { 
                ProSimDataRef = new ProSim.ProSimDataRef()
                {
                    Path = DatarefPathTextBox.Text.Trim()
                }
            };
            transformOptionsGroup1.syncToConfig(config);
        }

        internal void syncFromConfig(OutputConfigItem config)
        {
            if (!(config.Source is Base.ProSimSource)) return;

            DatarefPathTextBox.Text = (config.Source as Base.ProSimSource).ProSimDataRef.Path;
            transformOptionsGroup1.syncFromConfig(config);

            // Load datarefs when panel is shown, in case Load event fired before Init
            LoadDataRefDescriptions();
        }

        public void LoadDataRefDescriptions()
        {
            LogDetailed($"ProSimDataRefPanel LoadDataRefDescriptions. OutputMode={_isOutputMode}");
            if (_executionManager == null)
            {
                LogDetailed("ProSimDataRefPanel LoadDataRefDescriptions aborted: execution manager is null.");
                return; // Silently return if not initialized
            }

            var proSimCache = _executionManager.GetProSimCache();
            if (!proSimCache.IsConnected())
            {
                LogDetailed("ProSimDataRefPanel LoadDataRefDescriptions aborted: ProSim cache not connected.");
                return; // Silently return if not connected
            }

            try
            {
                _isLoading = true;
                // Get the dataref descriptions from the already-connected ProSimCache
                _dataRefDescriptions = proSimCache.GetDataRefDescriptions();
                LogDetailed($"ProSimDataRefPanel fetched {_dataRefDescriptions.Count} datarefs from cache.");
                _canReadDataRefDescriptions = _dataRefDescriptions.Values
                    .Where(drd => _isOutputMode ? drd.CanRead : drd.CanWrite)
                    .ToList();
                if (!_isOutputMode && _canReadDataRefDescriptions.Count == 0 && _dataRefDescriptions.Count > 0)
                {
                    LogDetailed("No writable ProSim datarefs reported; showing all datarefs.", LogSeverity.Warn);
                    _canReadDataRefDescriptions = _dataRefDescriptions.Values.ToList();
                }
                LogDetailed($"ProSimDataRefPanel filtered to {_canReadDataRefDescriptions.Count} datarefs.");

                if (_dataRefDescriptions.Count == 0)
                {
                    ScheduleDataRefRetry();
                }
                else
                {
                    ResetDataRefRetry();
                }

                if (_dataRefDescriptions.Count > 0)
                {
                    // Marshal the UI update to the main thread
                    if (this.InvokeRequired)
                    {
                        this.Invoke(new System.Action(() =>
                        {
                            _canReadDataRefDescriptions.Sort((drd1, drd2) => drd2.Name.CompareTo(drd1.Name));
                            dataGridView1.DataSource = null;
                            dataGridView1.DataSource = _canReadDataRefDescriptions;
                            LogDetailed("ProSimDataRefPanel data grid updated on UI thread.");
                        }));
                    }
                    else
                    {
                        _canReadDataRefDescriptions.Sort((drd1, drd2) => drd2.Name.CompareTo(drd1.Name));
                        dataGridView1.DataSource = null;
                        dataGridView1.DataSource = _canReadDataRefDescriptions;
                        LogDetailed("ProSimDataRefPanel data grid updated on current thread.");
                        SelectRowForCurrentPath();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Instance.log($"Error retrieving ProSim dataref descriptions: {ex.Message}", LogSeverity.Error);
            }
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {
            // Return early if datarefs haven't been loaded yet
            if (_canReadDataRefDescriptions == null)
            {
                return;
            }

            if (!textBox1.Text.IsNullOrEmpty()) {
                var words = textBox1.Text.Split(' ').Select(w => w.ToLower()).ToArray();
                _canReadDataRefDescriptionsFiltered = _canReadDataRefDescriptions
                    .Where(drd => words.All(drd.Name.ToLower().Contains)
                    || words.All(drd.Description.ToLower().Contains)
                    || (words.Length > 1 && drd.Name.ToLower().Contains(words[0]) && words.Skip(1).All(drd.Description.ToLower().Contains))).ToList();
                dataGridView1.DataSource = _canReadDataRefDescriptionsFiltered;
                SelectRowForCurrentPath();
            } else
            {
                dataGridView1.DataSource = _canReadDataRefDescriptions;
                SelectRowForCurrentPath();
            }
        }

        private void dataGridView1_RowEnter(object sender, DataGridViewCellEventArgs e)
        {
            if (!_isLoading && dataGridView1.SelectedRows.Count > 0)
            {
                var drd = dataGridView1.Rows[e.RowIndex].DataBoundItem as DataRefDescription;
                if (drd != null)
                {
                    DatarefPathTextBox.Text = drd.Name;
                }
            }
        }

        private void DataRefRetryTimer_Tick(object sender, EventArgs e)
        {
            _dataRefRetryTimer.Stop();
            LogDetailed($"ProSimDataRefPanel retry tick {_dataRefRetryCount}/{MaxDataRefRetryCount}.");
            LoadDataRefDescriptions();
        }

        private void ScheduleDataRefRetry()
        {
            if (_dataRefRetryTimer.Enabled || _dataRefRetryCount >= MaxDataRefRetryCount)
            {
                if (_dataRefRetryCount >= MaxDataRefRetryCount)
                {
                    LogDetailed("ProSimDataRefPanel retry limit reached; stopping retries.");
                }
                return;
            }

            _dataRefRetryCount++;
            LogDetailed($"ProSimDataRefPanel scheduling retry {_dataRefRetryCount}/{MaxDataRefRetryCount}.");
            _dataRefRetryTimer.Start();
        }

        private void ResetDataRefRetry()
        {
            _dataRefRetryCount = 0;
            if (_dataRefRetryTimer.Enabled)
            {
                _dataRefRetryTimer.Stop();
            }
            LogDetailed("ProSimDataRefPanel retry reset.");
        }

        private void SelectRowForCurrentPath()
        {
            var path = DatarefPathTextBox.Text?.Trim();
            if (string.IsNullOrEmpty(path) || dataGridView1.Rows.Count == 0)
            {
                return;
            }

            foreach (DataGridViewRow row in dataGridView1.Rows)
            {
                if (row.DataBoundItem is DataRefDescription dataRef &&
                    string.Equals(dataRef.Name, path, StringComparison.OrdinalIgnoreCase))
                {
                    dataGridView1.ClearSelection();
                    row.Selected = true;
                    if (row.Index >= 0 && row.Index < dataGridView1.RowCount)
                    {
                        dataGridView1.FirstDisplayedScrollingRowIndex = row.Index;
                    }
                    LogDetailed($"ProSimDataRefPanel selected dataref '{path}'.");
                    break;
                }
            }
        }
    }
} 
