using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Newtonsoft.Json;
using ScottPlot;
using ScottPlot.Plottable;
using ScottPlot.Renderable;
using SerialOscilloscope.Utils;
using SerialOscilloscope.Utils.TI;
using Serilog;
using Sofar.LoggerManager;

namespace SerialOscilloscope.ViewModels
{
    public partial class OscilloscopeVm: ObservableObject
    {
        private ILogger logger = LoggerManager.Instance.DefaultLogger;

        public OscilloscopeVm(WpfPlot plotCtrl) {
            _plotCtrl = plotCtrl;
            InitializePlot();
            InitializeChannelSettings();
            InitializeTimers();
        }


       

        private string _debugText1;
        public string DebugText1 {
            get { return _debugText1; }
            set { _debugText1 = value; OnPropertyChanged();}
        }

        #region Control settings
        
        private TIAddressChecker _addressChecker = new TIAddressChecker();

        private string _coffPath = "";
        public string CoffPath {
            get => _coffPath; 
            set { _coffPath = value; OnPropertyChanged();}
        }
        public string DwarfXmlPath {
            get => _addressChecker.DwarfXmlPath;
            set { _addressChecker.DwarfXmlPath = value; }
        }

        private RelayCommand _importCoffCommand;
        public RelayCommand ImportCoffCommand {
            get {
                if (_importCoffCommand == null)
                    _importCoffCommand = new RelayCommand(ImportCOFF);
                return _importCoffCommand;
            }
            set { _importCoffCommand = value;}
        }

        #region Communication
        private OscilloSerialConnection _oscilloCom = new OscilloSerialConnection();

        public string SerialPort {
            get => _oscilloCom.PortName;
            set {
                if (OscilloSerialConnection.GetAvailablePort().Any(elem => elem == value)) {
                    _oscilloCom.PortName = value;
                    OnPropertyChanged();
                }
            }
        }

        public int SerialBaudRate {
            get => _oscilloCom.PortBaud;
            set {
                _oscilloCom.PortBaud = value;
            }
        }
        #endregion

        public int SamplingMode { get; set; } = 1;
        public int SamplingRate { get; set; } = 20000;
        public int ChannelNum { get; set; } = 4;
        public ObservableCollection<ChannelModel> ChannelsSettings { get; set; }
        public ObservableCollection<string> DataTypeComboItems { get; set; }
        public ObservableCollection<string> DataScaleComboItems { get; set; }
        public ObservableCollection<string> AvailablePortsComboItems { get; set; }

        private RelayCommand _startCommand;
        public RelayCommand StartCommand {
            get { return _startCommand ??= new RelayCommand(StartRunning); }
            set { _startCommand = value; }
        }

        private RelayCommand _stopCommand;
        public RelayCommand StopCommand {
            get {
                if (_stopCommand == null)
                    _stopCommand = new RelayCommand(StopRunning);
                return _stopCommand;
            }
            set { _stopCommand = value; }
        }
        #endregion

        public ObservableCollection<string> DataToWriteTypeComboItems { get; set; }
        public ObservableCollection<string> DataToWriteScaleComboItems { get; set; }
        public ObservableCollection<ChannelModel> VariablesToWrite { get; set; }
        private RelayCommand _writeCommand;
        public RelayCommand WriteCommand {
            get {
                if (_writeCommand == null)
                    _writeCommand = new RelayCommand(WriteVariables);
                return _writeCommand;
            }
            set { _writeCommand = value; }
        }

        private int _timeUnitScale = 1;
        public int TimeUnitScale {
            get { return _timeUnitScale; }
            set {
                _timeUnitScale = value;
                OnPropertyChanged();
            }
        }

        #region Plot
        private WpfPlot _plotCtrl;
        private List<IProducerConsumerCollection<UInt16>> _signalBuffersList;
        private List<double[]> _signalRawDataList;
        private List<double[]> _signalDataList;
        private List<ScottPlot.Plottable.SignalPlot?> _signalPlotList;
        private List<ScottPlot.Renderable.Axis?> _signalAxisList;
        private List<ScottPlot.Plottable.ArrowCoordinated?> _signalArrowList;
        private List<ScottPlot.Plottable.MarkerPlot?> _signalClickMarkers;
        public ObservableCollection<ChannelTextModel> ChannelTexts { get; set; }

        private readonly double _timeIntervals = 10;
        private readonly int _maxPoints = 30000;  //32768;
        private int _fillBeginIdx = 0;
        private double _timeX = 0;

        private int _timeUnitScalePrev = 1;   // 节约绘图更新开销
        private List<int> _dataPlotScalePrevList; 

        private DispatcherTimer _renderTimer;
        private DispatcherTimer _configTimer;
        private DispatcherTimer _arrowTimer;
        private Thread _revThread;
        private bool _isRendering = false;
        public bool IsRendering {
            get => _isRendering;
            set { _isRendering = value; OnPropertyChanged();}
        }
        #endregion  

       

        #region Methods
        public double GetSampleRateByMode() {
            switch (SamplingMode) {
                case 0: return SamplingRate;
                case 1: return SamplingRate / (double)(3 + 2 * ChannelNum);
                default: return SamplingRate;
            }
        }

        public void InitializeChannelSettings() {
            AvailablePortsComboItems = new ObservableCollection<string>(OscilloSerialConnection.GetAvailablePort());
            DataTypeComboItems = new ObservableCollection<string>() { "U16", "I16", "Float" };
            DataScaleComboItems = new ObservableCollection<string>() { "x1", "x10", "x100", "x1k", "x10k", "x100k"};
            DataToWriteTypeComboItems = new ObservableCollection<string>() { "I16", "Float" };
            DataToWriteScaleComboItems = new ObservableCollection<string>() { "x1", "x10", "x100", "x1k", "x10k", "x100k", "x1000k"};

            VariablesToWrite = new ObservableCollection<ChannelModel>();
            VariablesToWrite.Add(new ChannelModel());

            if (!LoadSettings()) {
                ChannelsSettings = new ObservableCollection<ChannelModel>();
                ChannelsSettings.Add(new ChannelModel() {
                    TagName = "CH1",
                    LineColor = System.Drawing.Color.FromArgb(unchecked((int)0xffffff32)),
                });
                ChannelsSettings.Add(new ChannelModel() {
                    TagName = "CH2",
                    LineColor = System.Drawing.Color.FromArgb(unchecked((int)0xff3232ff)),
                });
                ChannelsSettings.Add(new ChannelModel() {
                    TagName = "CH3",
                    LineColor = System.Drawing.Color.FromArgb(unchecked((int)0xffff3232)),
                });
                ChannelsSettings.Add(new ChannelModel() {
                    TagName = "CH4",
                    LineColor = System.Drawing.Color.FromArgb(unchecked((int)0xff32ff32)),
                });
            }

            ChannelTexts = new ObservableCollection<ChannelTextModel>();
            for (int i = 0; i < ChannelNum; i++) {
                ChannelTexts.Add(new ChannelTextModel(){TextColor = ChannelsSettings[i].LineColor });
            }
            
        }

        public void InitializePlot() {
           // Style and Axis
            _plotCtrl.Configuration.Zoom = false;
            _plotCtrl.Configuration.DoubleClickBenchmark = false;
            _plotCtrl.Plot.Style(ScottPlot.Style.Black);

            _plotCtrl.Plot.BottomAxis.Dims.SetAxis(0 - 0.1 / TimeUnitScale, (_timeIntervals + 0.1) / TimeUnitScale);
            _plotCtrl.Plot.BottomAxis.ManualTickSpacing(1.0 / TimeUnitScale);
            _plotCtrl.Plot.BottomAxis.TickMarkDirection(false);
            _plotCtrl.Plot.BottomAxis.Layout(maximumSize: 15);

            _plotCtrl.Plot.TopAxis.Hide();

            _plotCtrl.Plot.LeftAxis.Dims.SetAxis(-5, 5);
            _plotCtrl.Plot.LeftAxis.MinimumTickSpacing(1);
            _plotCtrl.Plot.LeftAxis.LockLimits(true);
            _plotCtrl.Plot.LeftAxis.Hide();

            _plotCtrl.Plot.RightAxis.Hide();

            // Data and Plottable lists
            _signalBuffersList = new List<IProducerConsumerCollection<UInt16>>();
            _signalRawDataList = new List<double[]>();
            _signalDataList = new List<double[]>();
            _signalPlotList = new ();
            _signalAxisList = new ();
            _signalArrowList = new ();
            _signalClickMarkers = new ();
            
            _dataPlotScalePrevList = new List<int>();
            for (int i = 0; i < ChannelNum; i++) {
                _dataPlotScalePrevList.Add(1);
            }

            // MouseEvent 
            _plotCtrl.LeftClicked += LeftClickToReadCoordinates;
        }

        public void InitializeTimers() {
            _renderTimer = new DispatcherTimer();
            _renderTimer.Interval = TimeSpan.FromMilliseconds(250);
            _renderTimer.Tick += Render;
            _renderTimer.Start();

            _configTimer = new DispatcherTimer();
            _configTimer.Interval = TimeSpan.FromMilliseconds(100);
            _configTimer.Tick += UpdatePlotConfig;
            _configTimer.Start();

            _arrowTimer = new DispatcherTimer();
            _arrowTimer.Interval = TimeSpan.FromMilliseconds(50);
            _arrowTimer.Tick += UpdateArrowsPosition;
            _arrowTimer.Start(); 
        }

        public void CloseTimers() {
            _renderTimer?.Stop();
            _arrowTimer?.Stop();
            _configTimer?.Stop();
            _oscilloCom.ClosePort();
        }

        #region Commands and Functionalities

        private const int DwarfXmlValidHours = 12;

        private bool _isStartAllowed = true;
        public bool IsStartAllowed
        {
            get => _isStartAllowed;
            set => SetProperty(ref _isStartAllowed, value);
        }

        private async void ImportCOFF() {
            OpenFileDialog dlg = new OpenFileDialog();
            dlg.Title = "选择COFF(.out)文件";
            dlg.Filter = "out(*.out)|*.out";
            dlg.Multiselect = false;
            dlg.RestoreDirectory = true;
            if (dlg.ShowDialog() != true)
                return;

            var saveDir = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp", "DWARF"));
            string objFileName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);

            string copyObjPath = System.IO.Path.Combine(saveDir.FullName, objFileName + "(copy).out");
            System.IO.File.Copy(dlg.FileName, copyObjPath);

            string dwarfXmlSavePath = System.IO.Path.Combine(saveDir.FullName, objFileName + "_DWARF_" +
                                                                               DateTime.Now.ToString("yyyyMMddhhmmss") + AppDomain.CurrentDomain.Id + ".xml");
            try
            {
                CoffPath = "加载中...";
                IsStartAllowed = false;  // 加载中禁止启动
                await TIPluginHelper.ConvertCoffToXmlAsync(copyObjPath, dwarfXmlSavePath, saveDir.FullName);
                await Task.Run(() => TIAddressChecker.TrimDwarfXml(dwarfXmlSavePath));

                MessageBox.Show($"Debug信息文件DWARF.xml保存路径（有效时间{DwarfXmlValidHours}小时）:\n" + dwarfXmlSavePath,
                    "导入成功", MessageBoxButton.OK, MessageBoxImage.Information);
                CoffPath = dlg.FileName;
                DwarfXmlPath = dwarfXmlSavePath;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "导入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                CoffPath = "";
            }
            finally
            {
                System.IO.File.Delete(copyObjPath);
                IsStartAllowed = true;
            }
        }

        private void StartRunning() {
            logger.Information("启动");

            // 加载dwarf.xml文件，查找变量地址
            if (string.IsNullOrEmpty(DwarfXmlPath) || !CheckDwarfXmlDate())
            {
                MessageBox.Show("DWARF.xml文件不存在或已过期，请重新加载.out文件", "文件过期",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                IsStartAllowed = true;
                return;
            }

            if (CheckAddresses(ChannelsSettings) <= 0) {
                return;
            }

            // 打开串口，开始接收数据
            if (!OpenSerialPort()) return;
            MessageBox.Show("Started.");

            // 设置各通道的绘图参数
            SetupChannelsPlot();

            // 开始
            IsRendering = true;

        }

        private bool CheckDwarfXmlDate() {
            var dwarfFileInfo = new System.IO.FileInfo(DwarfXmlPath);
            var lastWriteTime = dwarfFileInfo.LastWriteTime;
            var currentTime = DateTime.Now;
            var timeSlot = currentTime - lastWriteTime;
            if (timeSlot < TimeSpan.FromMinutes(100)) {
                return true;
            }
            dwarfFileInfo.Delete();
            return false;
        }

        private int CheckAddresses(ObservableCollection<ChannelModel> varCollection) {
            if (!_addressChecker.IsDwarfXmlLoaded())
                _addressChecker.LoadDwarfXml(DwarfXmlPath);
            var addrCheckTasks = new List<Task>();

            string errInfo = "";

            for (int i = 0; i < varCollection.Count; i++) {
                varCollection[i].VariableAddress = 0;

                if (string.IsNullOrWhiteSpace(varCollection[i].VariableName))
                {
                    varCollection[i].ID = -1;
                    varCollection[i].IsInvalid = false;
                }

                if (varCollection[i].VariableName.Length > 0) {
                    int sn = i;
                    addrCheckTasks.Add(Task.Run(() => {
                        UInt32 addrRes = _addressChecker.SearchAddressByName(varCollection[sn].VariableName, sn);
                        varCollection[sn].VariableAddress = addrRes;
                    }));
                } 
            }

            try {
                Task.WhenAll(addrCheckTasks).Wait();

            }
            catch (AggregateException ae) {
                foreach (var ex in ae.Flatten().InnerExceptions) {
                    if (ex is FormatException) {
                        string exMsg = ex.Message;
                        int snIdx = exMsg.IndexOf("VarNameSN:");
                        if (snIdx > 0) {
                            int varSn = int.Parse(exMsg.Substring(snIdx + "VarNameSN:".Length));
                            MessageBox.Show(exMsg.Substring(0, snIdx) +
                                            "--" + varCollection[varSn].VariableName + "--" + varSn,
                                "变量格式错误", MessageBoxButton.OK, MessageBoxImage.Error);
                            return -1;
                        }
                        else throw;
                    }
                    else if (ex is NullReferenceException || ex is ArgumentNullException) {
                        string exMsg = ex.Message;
                        int snIdx = exMsg.IndexOf("VarNameSN:");
                        if (snIdx > 0) {
                            int varSn = int.Parse(exMsg.Substring(snIdx + "VarNameSN:".Length));
                            MessageBox.Show(exMsg.Substring(0, snIdx) +
                                            "--" + varCollection[varSn].VariableName + "--" + varSn,
                                "寻址失败", MessageBoxButton.OK, MessageBoxImage.Error);
                            varCollection[varSn].VariableAddress = 0;
                            return -1;
                        }
                        else throw;

                    }
                    else throw;
                }
            }
            finally {
                _addressChecker.UnloadDwarfXml();
            }

            string addrResDebug = "";
            int activatedCount = 0;
            for (int i = 0; i < varCollection.Count; i++) {
                if (varCollection[i].VariableAddress > 0) {
                    varCollection[i].ID = i + 1;     // 无信号为-1，有信号从1开始。
                    activatedCount++;
                }
                else {
                    varCollection[i].ID = -1;
                }
                addrResDebug += $"{varCollection[i].VariableName}: {varCollection[i].VariableAddress} \n";
            }
            MessageBox.Show(addrResDebug);

            return activatedCount;
        }

        private bool OpenSerialPort() {
            //_oscilloCom.PortName = "COM3";
            //_oscilloCom.PortBaud = 250000;

            _oscilloCom.SetupPort();

            if (!_oscilloCom.OpenPort()) {
                MessageBox.Show("Failed to open port.");
                return false;
            }


            var addrResults = new UInt32[ChannelsSettings.Count];
            var dataTypes = new int[ChannelsSettings.Count];
            var floatScales = new int[ChannelsSettings.Count];
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                addrResults[i] = ChannelsSettings[i].VariableAddress;
                dataTypes[i] = ChannelsSettings[i].DataType;
                floatScales[i] = ChannelsSettings[i].FloatDataScale;
            }

            if (!_oscilloCom.SendConfigCmd(addrResults, SamplingMode, dataTypes, floatScales)) {
                MessageBox.Show("Failed to send configuration data.");
                return false;
            }
            
            _oscilloCom.SendStartCmd();


            #region Receive Data
            var buffers = new List<IProducerConsumerCollection<UInt16>>();
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (_signalBuffersList.Count < ChannelsSettings.Count)
                    _signalBuffersList.Add(null);
                if (ChannelsSettings[i].ID > 0) {
                    _signalBuffersList[i] = new ConcurrentQueue<UInt16>();
                    buffers.Add(_signalBuffersList[i]);
                }
                else {
                    _signalBuffersList[i] = null;
                }
            }
            _oscilloCom.StartReceivingThread(buffers);
            

            return true;

            #endregion

        }

        private void SetupChannelsPlot() {
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (_signalPlotList.Count < ChannelsSettings.Count) {
                    _signalRawDataList.Add(null);
                    _signalDataList.Add(null);
                    _signalPlotList.Add(null);
                    _signalAxisList.Add(null);
                    _signalArrowList.Add(null);
                    _signalClickMarkers.Add(null);
                }

                if (ChannelsSettings[i].ID > 0) {
                    if (_signalPlotList[i] == null) {
                        _signalRawDataList[i] = new double[_maxPoints];
                        _signalDataList[i] = new double[_maxPoints];
                        _signalAxisList[i] = _plotCtrl.Plot.AddAxis(Edge.Right, axisIndex: ChannelsSettings[i].ID + 1);  // 自定义数据轴只能从2开始
                        _signalPlotList[i] = _plotCtrl.Plot.AddSignal(ys: _signalDataList[i]);
                    }

                    _signalAxisList[i].Color(ChannelsSettings[i].LineColor);
                    _signalAxisList[i].Dims.SetAxis(min: -5, max: 5);
                    _signalAxisList[i].Hide();

                    _signalPlotList[i].YAxisIndex = ChannelsSettings[i].ID + 1;
                    _signalPlotList[i].Color = ChannelsSettings[i].LineColor;
                    _signalPlotList[i].LineWidth = 5;
                    _signalPlotList[i].SampleRate = GetSampleRateByMode();
                    _signalPlotList[i].MaxRenderIndex = 0;

                }
                else {
                    _signalDataList[i] = null;
                    if (_signalAxisList[i] != null) {
                        _plotCtrl.Plot.RemoveAxis(_signalAxisList[i]);
                        _signalAxisList[i] = null;
                    }

                    if (_signalPlotList[i] != null) {
                        _plotCtrl.Plot.Remove(_signalPlotList[i]);
                        _signalPlotList[i] = null;
                    }

                }

                if (_signalClickMarkers[i] != null) {
                    _plotCtrl.Plot.Remove(_signalClickMarkers[i]);
                    _signalClickMarkers[i] = null;
                    ChannelTexts[i].SignalText = "";
                }
            }
            _plotCtrl.Refresh();
        }

        private void StopRunning() {
            _isRendering = false;
            _fillBeginIdx = 0;
            _timeX = 0;
            _oscilloCom.StopReceivingThread();
            
            IsRendering = false;
        }

        private void WriteVariables() {
            // 加载dwarf.xml文件，查找变量地址
            if (string.IsNullOrEmpty(DwarfXmlPath) || !CheckDwarfXmlDate()) {
                MessageBox.Show("DWARF.xml文件不存在或已过期，请重新加载.out文件", "文件过期",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (CheckAddresses(VariablesToWrite) <= 0) {
                return;
            }
            // 处理变量值
            var varData = new List<float?>();
            for (int i = 0; i < VariablesToWrite.Count; i++) {
                varData.Add(null);
                if (VariablesToWrite[i].ID <= 0) 
                    continue;
                float value;
                if (float.TryParse(VariablesToWrite[i].DataValueString, out value)) {
                    varData[i] = value;
                }
                else {
                    VariablesToWrite[i].ID = -1;
                }
            }
            // 检查串口
            bool IsPortReady = _oscilloCom.IsOpen();
            if (!IsPortReady) {
                _oscilloCom.SetupPort();
                _oscilloCom.OpenPort();
            }
            // 发送数据
            if (IsPortReady || _oscilloCom.IsOpen()) {
                for (int i = 0; i < VariablesToWrite.Count; i++) {
                    if (VariablesToWrite[i].ID <= 0) 
                        continue;
                    string msg = $"是否写入变量：\n" +
                                 $"Name:{VariablesToWrite[i].VariableName}\n" +
                                 $"Address:{VariablesToWrite[i].VariableAddress.ToString("X8")}\n" +
                                 $"Value:{varData[i].Value} Type:{DataToWriteTypeComboItems[VariablesToWrite[i].DataType]}" + 
                                 $"Scale:{DataToWriteScaleComboItems[VariablesToWrite[i].FloatDataScale]}";
                    var confirmRes = MessageBox.Show(msg, caption: "写入变量确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (confirmRes == MessageBoxResult.Yes) {
                        _oscilloCom.Write32BitVariable(varData[i].Value, VariablesToWrite[i].VariableAddress,
                            VariablesToWrite[i].DataType, VariablesToWrite[i].FloatDataScale);
                        Thread.Sleep(50);
                    }
                }
            }

            if (!IsPortReady) {
                _oscilloCom.ClosePort();
            }

        }

        #region Plot Rendering
        private void Render(object sender, EventArgs args) {
            if (!_isRendering) return;

            int newPointNum = int.MaxValue;
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (ChannelsSettings[i].ID > 0)
                    newPointNum = Math.Min(newPointNum, _signalBuffersList[i].Count);
            }
            DebugText1 = newPointNum.ToString();

            if (_fillBeginIdx + newPointNum > _maxPoints) {
                int moveLen = _fillBeginIdx + newPointNum - _maxPoints;
                for (int i = 0; i < ChannelsSettings.Count; i++) {
                    if (ChannelsSettings[i].ID > 0) {
                        Array.Copy(_signalRawDataList[i], moveLen, _signalRawDataList[i], 0, _fillBeginIdx - moveLen);
                        Array.Copy(_signalDataList[i], moveLen, _signalDataList[i], 0, _fillBeginIdx - moveLen);
                    }
                }
                _fillBeginIdx -= moveLen;
            }

            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (ChannelsSettings[i].ID <= 0)
                    continue;
                for (int p = _fillBeginIdx; p < _fillBeginIdx + newPointNum; p++) {
                    UInt16 rawData = 0;
                    _signalBuffersList[i].TryTake(out rawData);
                    switch (ChannelsSettings[i].DataType) {
                        case 0: _signalRawDataList[i][p] = (UInt16)rawData; break;
                        case 1: _signalRawDataList[i][p] = (Int16)rawData; break;
                        case 2: _signalRawDataList[i][p] = (Int16)rawData; break;
                    }

                }

            }
            _fillBeginIdx += newPointNum;

            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (ChannelsSettings[i].ID < 0)
                    continue;
                int dataPlotScale = ChannelsSettings[i].DataPlotScale;
                for (int p = 0; p < _maxPoints; p++) {
                    _signalDataList[i][p] = _signalRawDataList[i][p] / dataPlotScale;
                }
            }


            double timeStep = 1.0 / GetSampleRateByMode();
            _timeX += newPointNum * timeStep;
            int maxRenderIdx = (_timeX / timeStep <= _maxPoints) ? (int)(_timeX / timeStep) - 1 : _maxPoints - 1;
            maxRenderIdx = maxRenderIdx >= 0 ? maxRenderIdx : 0;
            double offsetX = (maxRenderIdx == _maxPoints - 1) ? (_timeX - _maxPoints * timeStep) : 0;
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (ChannelsSettings[i].ID > 0) {
                    _signalPlotList[i].MaxRenderIndex = maxRenderIdx;
                    _signalPlotList[i].OffsetX = offsetX;
                }

            }

            double curXMax = _plotCtrl.Plot.GetAxisLimits().XMax;
            if (_timeX > curXMax || _timeX > _timeIntervals / TimeUnitScale) {
                _plotCtrl.Plot.SetAxisLimitsX(xMin: _timeX - _timeIntervals / TimeUnitScale, xMax: _timeX);
            }
            else {
                _plotCtrl.Plot.SetAxisLimitsX(xMin: 0, xMax: _timeIntervals / TimeUnitScale);
            }
            _plotCtrl.Plot.XAxis.ManualTickSpacing(1.0 / TimeUnitScale);
            _plotCtrl.Refresh();


        }

        private void UpdatePlotConfig(object sender, EventArgs args) {
            bool HasChanged = false;
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (_signalPlotList != null && _signalPlotList.Count == ChannelsSettings.Count) {
                    if (ChannelsSettings[i].ID > 0 && _signalAxisList[i] != null
                        && _signalPlotList[i] != null && _signalDataList[i] != null) {
                        _signalAxisList[i].LockLimits(!ChannelsSettings[i].IsSelected);   // Y方向拖动

                        if (_signalPlotList[i].IsVisible != ChannelsSettings[i].IsVisible) { // 可见性
                            _signalPlotList[i].IsVisible = ChannelsSettings[i].IsVisible;
                            HasChanged = true;
                        }

                        if (!_isRendering && ChannelsSettings[i].DataPlotScale != _dataPlotScalePrevList[i]) {
                            // 静态时变更显示倍率
                            for (int p = 0; p < _maxPoints; p++) {
                                _signalDataList[i][p] = _signalRawDataList[i][p] / ChannelsSettings[i].DataPlotScale;
                            }
                            _dataPlotScalePrevList[i] = ChannelsSettings[i].DataPlotScale;
                            HasChanged = true;

                        }
                    }
                }
            }

            if (!_isRendering && TimeUnitScale != _timeUnitScalePrev) {
                // 静态时变更时间轴单位
                double curXMid = _plotCtrl.Plot.GetAxisLimits().XCenter;
                double newXLim = Math.Max(curXMid - 0.5 * _timeIntervals / TimeUnitScale, 0);
                double newYLim = newXLim + _timeIntervals / TimeUnitScale;
                _plotCtrl.Plot.SetAxisLimitsX(xMin: newXLim, xMax: newYLim);
                _plotCtrl.Plot.XAxis.ManualTickSpacing(1.0 / TimeUnitScale);
                _timeUnitScalePrev = TimeUnitScale;
                HasChanged = true;
            }

            if (HasChanged)
                _plotCtrl.Refresh();

            //CursorSliderUpdate();
        }

        private void UpdateArrowsPosition(object sender, EventArgs args) {
            // 波形位置指示箭头
            double xSpan = _plotCtrl.Plot.XAxis.Dims.Span;
            double xCurMin = _plotCtrl.Plot.XAxis.Dims.Min;
            double arrowLen = xSpan * 0.05;
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (_signalArrowList != null && _signalArrowList.Count == ChannelsSettings.Count) {
                    if (ChannelsSettings[i].ID < 0) {
                        if (_signalArrowList[i] != null)
                            _plotCtrl.Plot.Remove(_signalArrowList[i]);
                        continue;
                    }

                    double yiInY0 = _plotCtrl.Plot.YAxis.Dims.GetUnit(_signalAxisList[i].Dims.GetPixel(0));
                    if (yiInY0 >= 5.0) yiInY0 = 4.8;
                    if (yiInY0 <= -5.0) yiInY0 = -4.8;
                    double xArrowBase = xCurMin + 0.005 * xSpan * i;

                    if (_signalArrowList[i] != null) {
                        if (Math.Abs(_signalArrowList[i].Base.Y - yiInY0) < 0.0001
                            && Math.Abs(_signalArrowList[i].Base.X - xArrowBase) < 0.0001)
                            continue;
                        _plotCtrl.Plot.Remove(_signalArrowList[i]);
                    }

                    //_signalArrowList[i] = new ArrowCoordinated(xBase: xArrowBase, yBase: yiInY0,
                    //    xTip: xArrowBase + arrowLen, yTip: yiInY0);
                    //_signalArrowList[i].LineWidth = 4;
                    //_signalArrowList[i].LineColor = ChannelCtxList[i].LineColor;
                    //_signalArrowList[i].XAxisIndex = 1;

                    _signalArrowList[i] = _plotCtrl.Plot.AddArrow(xBase: xArrowBase, yBase: yiInY0,
                        xTip: xArrowBase + arrowLen, yTip: yiInY0,
                        lineWidth: 4, color: ChannelsSettings[i].LineColor);

                }

            }

            _plotCtrl.Refresh();
        }

        private void LeftClickToReadCoordinates(object sender, EventArgs args) {
            if (_signalPlotList.Count != ChannelsSettings.Count)
                return;
            float xClickPix, yClickPix;
            (xClickPix, yClickPix) = _plotCtrl.GetMousePixel();
            for (int i = 0; i < ChannelsSettings.Count; i++) {
                if (ChannelsSettings[i].ID <= 0 || !ChannelsSettings[i].IsVisible) {
                    continue;
                }
                double xSignal, ySignal, ySignalPix;
                (xSignal, ySignal, _) = _signalPlotList[i].GetPointNearestX(_plotCtrl.Plot.BottomAxis.Dims.GetUnit(xClickPix));
                ySignalPix = _signalAxisList[i].Dims.GetPixel(ySignal);
                if (Math.Abs(ySignalPix - yClickPix) < 8) {
                    string dataText = "";
                    double yReal = ySignal * ChannelsSettings[i].DataPlotScale;

                    if (ChannelsSettings[i].DataType == 2) {  // float
                        double scale = 1.0;
                        for (int k = 0; k < ChannelsSettings[i].FloatDataScale; k++) {
                            scale *= 0.1;
                        }

                        yReal *= scale;
                        dataText = $"CH{i + 1}: {yReal.ToString($"N{ChannelsSettings[i].FloatDataScale}")}";
                    }
                    else { // int16/uint16
                        dataText = $"CH{i + 1}: {(int)yReal}\n      " +
                                   $"0x{Convert.ToString((int)yReal, 16).PadLeft(4, '0')}\n";
                        string binStr = $"b{Convert.ToString((int)yReal, 2).PadLeft(16, '0')}";
                        for (int p = 5; p < binStr.Length; p += 5) {
                            binStr = binStr.Insert(p, "_");
                        }
                        dataText += binStr;
                    }
                    
                    if (_signalClickMarkers[i] != null) {
                        _plotCtrl.Plot.Remove(_signalClickMarkers[i]);
                    }
                    _signalClickMarkers[i] = new MarkerPlot() {
                        XAxisIndex = 0,
                        YAxisIndex = _signalPlotList[i].YAxisIndex,
                        X = xSignal,
                        Y = ySignal,
                        MarkerColor = ChannelsSettings[i].LineColor,
                        MarkerShape = MarkerShape.eks,
                        MarkerSize = 10,
                        MarkerLineWidth = 2,
                    };

                    ChannelTexts[i].SignalText = dataText;
                    _plotCtrl.Plot.Add(_signalClickMarkers[i]);
                    _plotCtrl.Refresh();

                }
            }
        }
        #endregion

        public void SaveSettings() {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingDir = System.IO.Path.Combine(currentDir, "Oscilloscope", "Settings");
            if (!System.IO.Directory.Exists(settingDir)) {
                System.IO.Directory.CreateDirectory(settingDir);
            }
            string settingFile = System.IO.Path.Combine(settingDir, "settings_" + DateTime.Now.ToString("yyyyMMddHHmmss") + 
                                                                    AppDomain.CurrentDomain.Id + ".json");
            if (!System.IO.File.Exists(settingFile)) {
                System.IO.FileInfo fileInfo = new System.IO.FileInfo(settingFile);
                fileInfo.Create().Close();
            }

            var dataToSave = new SaveDataModel() {
                CoffPath = this.CoffPath,
                DwarfXmlPath = this.DwarfXmlPath,
                SerialPort = this.SerialPort,
                SerialBaudRate = this.SerialBaudRate,
                SamplingMode = this.SamplingMode,
                SamplingRate = this.SamplingRate,
                TimeUnitScale = this.TimeUnitScale,
                ChannelsSettings = this.ChannelsSettings.ToList()
            };

            try {
                string jsonStr = JsonConvert.SerializeObject(dataToSave, Formatting.Indented);
                System.IO.File.WriteAllText(settingFile, jsonStr, Encoding.UTF8);
            }
            catch (Exception ex) {
                MessageBox.Show(ex.ToString());
                
            }
            


        }

        public bool LoadSettings() {
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            string settingFile = System.IO.Path.Combine(currentDir, "Oscilloscope", "Settings", "settings.json");
            if(!System.IO.File.Exists(settingFile)) 
                return false;
            var settingFileInfo = new System.IO.FileInfo(settingFile);
            var lastWriteTime = settingFileInfo.LastWriteTime;
            var currentTime = DateTime.Now;
            var timeSlot = currentTime - lastWriteTime;
            if (timeSlot > TimeSpan.FromMinutes(20)) {
                settingFileInfo.Delete();
                return false;
            }

            try {
                string jsonStr = "";
                using (System.IO.StreamReader sr = new StreamReader(settingFile, Encoding.UTF8)) {
                    jsonStr = sr.ReadToEnd();
                    sr.Close();
                }

                var dataRead = JsonConvert.DeserializeObject<SaveDataModel>(jsonStr);
                this.CoffPath = dataRead.CoffPath;
                this.DwarfXmlPath = dataRead.DwarfXmlPath;
                this.SerialPort = dataRead.SerialPort;
                this.SerialBaudRate = dataRead.SerialBaudRate;
                this.SamplingMode = dataRead.SamplingMode;
                this.SamplingRate = dataRead.SamplingRate;
                this.TimeUnitScale = dataRead.TimeUnitScale;
                this.ChannelsSettings = new ObservableCollection<ChannelModel>(dataRead.ChannelsSettings);
                return true;
            }
            catch (Exception ex){
                MessageBox.Show(ex.ToString());
                return false;
            }

        }

        #endregion

        
        #endregion
        
    }

    public class ChannelModel : ObservableObject
    {
        private string _tagName = "";
        public string TagName
        {
            get => _tagName;
            set { _tagName = value; OnPropertyChanged(); }
        }

        private System.Drawing.Color _lineColor = System.Drawing.Color.White;
        public System.Drawing.Color LineColor
        {
            get => _lineColor;
            set { _lineColor = value; OnPropertyChanged(); }
        }

        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        private bool _isSelected = false;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private string _variableName = "";
        public string VariableName
        {
            get => _variableName;
            set { _variableName = value; OnPropertyChanged(); }
        }

        private uint _variableAddress = 0;
        public uint VariableAddress
        {
            get => _variableAddress;
            set { _variableAddress = value; OnPropertyChanged(); }
        }

        private int _dataType = 7;
        public int DataType
        {
            get => _dataType;
            set { _dataType = value; OnPropertyChanged(); }
        }

        private int _floatDataScale = 0;
        public int FloatDataScale
        {
            get => _floatDataScale;
            set { _floatDataScale = value; OnPropertyChanged(); }
        }

        private bool _isInvalid = false;
        public bool IsInvalid
        {
            get => _isInvalid;
            set { _isInvalid = value; OnPropertyChanged(); }
        }

        private int _id = -1;
        public int ID
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        private string _comment = "";
        public string Comment
        {
            get => _comment;
            set { _comment = value; OnPropertyChanged(); }
        }


        private int _dataPlotScale = 1;
        public int DataPlotScale {
            get => _dataPlotScale;
            set {
                _dataPlotScale = value;
                OnPropertyChanged();
            }
        }

        

        public string DataValueString { get; set; } = ""; // For writing data

    }

    public class ChannelTextModel : ObservableObject {
        private string _signalText = "";
        public string SignalText {
            get { return _signalText; }
            set { _signalText = value; OnPropertyChanged();}
        }  
        public System.Drawing.Color TextColor { get; set; }
    }
    
    public class SaveDataModel {
        
        public List<ChannelModel> ChannelsSettings { get; set; }
        
        public string SerialPort { get; set; }
        
        public int SerialBaudRate { get; set; }
        
        public int SamplingMode { get; set; }
        public int SamplingRate { get; set; }

        public string CoffPath { get; set; }
        public string DwarfXmlPath { get; set; }

        public int TimeUnitScale { get; set; }
    }
    

    #region Value Converters
    public class TimeUnitSliderConverter : IValueConverter {
        public static List<int> TimeUnitScaleTable = new List<int>() { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000 };
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {

            if (targetType.Equals(typeof(double))) {
                // BackendValue(int) -> SliderValue(double)
                return TimeUnitScaleTable.FindIndex(i => i == (int)value);
            }
            else if (targetType.Equals(typeof(string))) {
                // BackendValue(int) -> TextboxText(string)

                int selectedTimeUnit = (int)value;
                string text;
                switch (selectedTimeUnit) {
                    case 1:
                        text = $"1s({selectedTimeUnit}x)"; break;
                    case 2:
                        text = $"500ms({selectedTimeUnit}x)"; break;
                    case 5:
                        text = $"200ms({selectedTimeUnit}x)"; break;
                    case 10:
                        text = $"100ms({selectedTimeUnit}x)"; break;
                    case 20:
                        text = $"50ms({selectedTimeUnit}x)"; break;
                    case 50:
                        text = $"20ms({selectedTimeUnit}x)"; break;
                    case 100:
                        text = $"10ms({selectedTimeUnit}x)"; break;
                    case 200:
                        text = $"5ms({selectedTimeUnit}x)"; break;
                    case 500:
                        text = $"2ms({selectedTimeUnit}x)"; break;
                    case 1000:
                        text = $"1ms({selectedTimeUnit}x)"; break;
                    case 2000:
                        text = $"500us({selectedTimeUnit}x)"; break;
                    default:
                        text = ""; break;
                }
                return text;
            }
            else {
                throw new NotImplementedException();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            // Slider -> BackendValue
            int idx;
            int.TryParse(value.ToString(), out idx);
            int selectedTimeUnit = TimeUnitScaleTable[idx];

            return selectedTimeUnit;

        }
    }

    public class PlotScaleSliderConverter : IValueConverter {
        public static List<int> ScalesList = new List<int>() { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000 };
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            if (targetType == typeof(double)) {
                // BackendValue(int) -> SliderValue(double)
                return ScalesList.FindIndex(i => i == (int)value);
            }
            else if (targetType == typeof(string)) {
                // BackendValue(int) -> TextboxText(string)
                string text = value.ToString() + "x";
                return text;
            }
            else {
                throw new NotImplementedException();
            }

        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            // Slider -> BackendValue
            int idx;
            int.TryParse(value.ToString(), out idx);
            int selectedScale = ScalesList[idx];
            return selectedScale;
        }
    }

    public class ChannelColorConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
            // System.Drawing.Color -> System.Windows.Media.Color
            System.Drawing.Color color = (System.Drawing.Color)value;
            var brush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B));
            return brush;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            throw new NotImplementedException();
        }
    }

    public class BoolInverseConverter : IValueConverter {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {

            if (targetType != typeof(bool))
                throw new InvalidOperationException("The target must be a boolean");

            return !(bool)value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
            if (targetType != typeof(bool))
                throw new InvalidOperationException("The target must be a boolean");

            return !(bool)value;
        }
    }

    #endregion

}
