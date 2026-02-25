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
            Log.Instance.log($"ProSimDataRefPanel initialized. OutputMode={_isOutputMode}", LogSeverity.Debug);
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
            Log.Instance.log($"ProSimDataRefPanel SetMode. OutputMode={_isOutputMode}", LogSeverity.Debug);
        }

        public void Init(IExecutionManager executionManager)
        {
            _executionManager = executionManager;
            Log.Instance.log("ProSimDataRefPanel Init called.", LogSeverity.Debug);
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
            Log.Instance.log($"ProSimDataRefPanel LoadDataRefDescriptions. OutputMode={_isOutputMode}", LogSeverity.Debug);
            if (_executionManager == null)
            {
                Log.Instance.log("ProSimDataRefPanel LoadDataRefDescriptions aborted: execution manager is null.", LogSeverity.Debug);
                return; // Silently return if not initialized
            }

            var proSimCache = _executionManager.GetProSimCache();
            if (!proSimCache.IsConnected())
            {
                Log.Instance.log("ProSimDataRefPanel LoadDataRefDescriptions aborted: ProSim cache not connected.", LogSeverity.Debug);
                return; // Silently return if not connected
            }

            try
            {
                _isLoading = true;
                // Get the dataref descriptions from the already-connected ProSimCache
                _dataRefDescriptions = proSimCache.GetDataRefDescriptions();
                Log.Instance.log($"ProSimDataRefPanel fetched {_dataRefDescriptions.Count} datarefs from cache.", LogSeverity.Debug);
                _canReadDataRefDescriptions = _dataRefDescriptions.Values
                    .Where(drd => _isOutputMode ? drd.CanRead : drd.CanWrite)
                    .ToList();
                if (!_isOutputMode && _canReadDataRefDescriptions.Count == 0 && _dataRefDescriptions.Count > 0)
                {
                    Log.Instance.log("No writable ProSim datarefs reported; showing all datarefs.", LogSeverity.Warn);
                    _canReadDataRefDescriptions = _dataRefDescriptions.Values.ToList();
                }
                Log.Instance.log($"ProSimDataRefPanel filtered to {_canReadDataRefDescriptions.Count} datarefs.", LogSeverity.Debug);

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
                            Log.Instance.log("ProSimDataRefPanel data grid updated on UI thread.", LogSeverity.Debug);
                        }));
                    }
                    else
                    {
                        _canReadDataRefDescriptions.Sort((drd1, drd2) => drd2.Name.CompareTo(drd1.Name));
                        dataGridView1.DataSource = null;
                        dataGridView1.DataSource = _canReadDataRefDescriptions;
                        Log.Instance.log("ProSimDataRefPanel data grid updated on current thread.", LogSeverity.Debug);
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
            Log.Instance.log($"ProSimDataRefPanel retry tick {_dataRefRetryCount}/{MaxDataRefRetryCount}.", LogSeverity.Debug);
            LoadDataRefDescriptions();
        }

        private void ScheduleDataRefRetry()
        {
            if (_dataRefRetryTimer.Enabled || _dataRefRetryCount >= MaxDataRefRetryCount)
            {
                if (_dataRefRetryCount >= MaxDataRefRetryCount)
                {
                    Log.Instance.log("ProSimDataRefPanel retry limit reached; stopping retries.", LogSeverity.Debug);
                }
                return;
            }

            _dataRefRetryCount++;
            Log.Instance.log($"ProSimDataRefPanel scheduling retry {_dataRefRetryCount}/{MaxDataRefRetryCount}.", LogSeverity.Debug);
            _dataRefRetryTimer.Start();
        }

        private void ResetDataRefRetry()
        {
            _dataRefRetryCount = 0;
            if (_dataRefRetryTimer.Enabled)
            {
                _dataRefRetryTimer.Stop();
            }
            Log.Instance.log("ProSimDataRefPanel retry reset.", LogSeverity.Debug);
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
                    Log.Instance.log($"ProSimDataRefPanel selected dataref '{path}'.", LogSeverity.Debug);
                    break;
                }
            }
        }
    }
} 
