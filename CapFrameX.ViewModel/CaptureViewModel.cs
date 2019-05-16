﻿using CapFrameX.Contracts.Configuration;
using CapFrameX.Contracts.PresentMonInterface;
using CapFrameX.OcatInterface;
using CapFrameX.PresentMonInterface;
using Gma.System.MouseKeyHook;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CapFrameX.ViewModel
{
    public class CaptureViewModel : BindableBase, INavigationAware
    {
        private const int PRESICE_OFFSET = 300;

        private readonly IAppConfiguration _appConfiguration;
        private readonly ICaptureService _captureService;

        private IDisposable _disposableHeartBeat;
        private IDisposable _disposableCaptureStream;
        private List<string> _captureData;
        private string _selectedProcessToCapture;
        private string _selectedProcessToIgnore;
        private bool _isAddToIgnoreListButtonActive = true;
        private bool _isCapturing;
        private bool _isCaptureModeActive = true;
        private string _captureStateInfo = string.Empty;
        private string _captureTimeString = "0";
        private string _captureStartDelayString = "0";
        private string _captureHotkeyString = "F12";
        private IKeyboardMouseEvents _globalHookEvent;

        public string SelectedProcessToCapture
        {
            get { return _selectedProcessToCapture; }
            set
            {
                _selectedProcessToCapture = value;
                RaisePropertyChanged();
                OnSelectedProcessToCaptureChanged();
            }
        }

        public string SelectedProcessToIgnore
        {
            get { return _selectedProcessToIgnore; }
            set
            {
                _selectedProcessToIgnore = value;
                RaisePropertyChanged();
                OnSelectedProcessToIgnoreChanged();
            }
        }

        public bool IsAddToIgnoreListButtonActive
        {
            get { return _isAddToIgnoreListButtonActive; }
            set
            {
                _isAddToIgnoreListButtonActive = value;
                RaisePropertyChanged();
            }
        }

        public string CaptureStateInfo
        {
            get { return _captureStateInfo; }
            set
            {
                _captureStateInfo = value;
                RaisePropertyChanged();
            }
        }

        public string CaptureTimeString
        {
            get { return _captureTimeString; }
            set
            {
                _captureTimeString = value;
                RaisePropertyChanged();
            }
        }

        public string CaptureStartDelayString
        {
            get { return _captureStartDelayString; }
            set
            {
                _captureStartDelayString = value;
                RaisePropertyChanged();
            }
        }

        public string CaptureHotkeyString
        {
            get { return _captureHotkeyString; }
            set
            {
                _captureHotkeyString = value;
                UpdateCaptureStateInfo();
                UpdateGlobalHookEvent();
                RaisePropertyChanged();
            }
        }

        public IAppConfiguration AppConfiguration => _appConfiguration;

        public ObservableCollection<string> ProcessesToCapture { get; }
            = new ObservableCollection<string>();

        public ObservableCollection<string> ProcessesToIgnore { get; }
            = new ObservableCollection<string>();

        public ICommand AddToIgonreListCommand { get; }

        public ICommand RemoveFromIgnoreListCommand { get; }

        public ICommand ResetCaptureProcessCommand { get; }

        public CaptureViewModel(IAppConfiguration appConfiguration, ICaptureService captureService)
        {
            _appConfiguration = appConfiguration;
            _captureService = captureService;

            AddToIgonreListCommand = new DelegateCommand(OnAddToIgonreList);
            RemoveFromIgnoreListCommand = new DelegateCommand(OnRemoveFromIgnoreList);
            ResetCaptureProcessCommand = new DelegateCommand(OnResetCaptureProcess);

            CaptureStateInfo = $"Capturing inactive... select process and press {CaptureHotkeyString} to start.";

            ProcessesToIgnore.AddRange(CaptureServiceConfiguration.GetProcessIgnoreList());
            _disposableHeartBeat = GetListUpdatHeartBeat();
            SubscribeToGlobalHookEvent();
            StartCaptureService();
        }

        public bool IsNavigationTarget(NavigationContext navigationContext)
        {
            return true;
        }

        public void OnNavigatedFrom(NavigationContext navigationContext)
        {
            _disposableHeartBeat?.Dispose();
            _isCaptureModeActive = false;
            StopCaptureService();
        }

        public void OnNavigatedTo(NavigationContext navigationContext)
        {
            _disposableHeartBeat?.Dispose();
            _disposableHeartBeat = GetListUpdatHeartBeat();
            _isCaptureModeActive = true;
            StartCaptureService();
        }

        private void SubscribeToGlobalHookEvent()
        {
            SetGlobalHookEventCaptureHotkey();
        }

        private void UpdateGlobalHookEvent()
        {
            if (_globalHookEvent != null)
            {
                _globalHookEvent.Dispose();
                SetGlobalHookEventCaptureHotkey();
            }
        }

        private void SetGlobalHookEventCaptureHotkey()
        {
            var onCombinationDictionary = new Dictionary<Combination, Action>
            {
                {Combination.FromString(CaptureHotkeyString), () =>
                {
                    if (_isCaptureModeActive)
                        SetCaptureMode();
                }}
            };

            _globalHookEvent = Hook.GlobalEvents();
            _globalHookEvent.OnCombination(onCombinationDictionary);
        }

        private void SetCaptureMode()
        {
            System.Media.SystemSounds.Beep.Play();

            if (!_isCapturing)
            {
                StartCaptureDataFromStream();

                _isCapturing = !_isCapturing;
                _disposableHeartBeat?.Dispose();
                IsAddToIgnoreListButtonActive = false;
                CaptureStateInfo = $"Capturing started... press {CaptureHotkeyString} to stop.";

                var context = TaskScheduler.FromCurrentSynchronizationContext();

                if (Convert.ToInt32(CaptureTimeString) > 0)
                {
                    Task.Run(async () =>
                    {
                        await SetTaskDelay().ContinueWith(_ =>
                       { FinishCapturingAndUpdateUi(); },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, context);
                    });
                }
            }
            else
            {
                FinishCapturingAndUpdateUi();
            }
        }

        private void FinishCapturingAndUpdateUi()
        {
            StopCaptureDataFromStream();
            System.Media.SystemSounds.Beep.Play();

            _isCapturing = !_isCapturing;
            _disposableHeartBeat = GetListUpdatHeartBeat();
            IsAddToIgnoreListButtonActive = true;
            UpdateCaptureStateInfo();
        }

        private async void StartCaptureDataFromStream()
        {
            _captureData = new List<string>();
            bool autoTermination = Convert.ToInt32(CaptureTimeString) > 0;
            double captureTime = Convert.ToInt32(CaptureTimeString);
            string firstDataLine = await _captureService.RedirectedOutputDataStream.FirstAsync();
            var firstLineSplit = firstDataLine.Split(',');
            double startTime = Convert.ToDouble(firstLineSplit[11], CultureInfo.InvariantCulture);

            _disposableCaptureStream = _captureService.RedirectedOutputDataStream
                .ObserveOn(new EventLoopScheduler()).Subscribe(dataLine =>
                {
                    if (string.IsNullOrWhiteSpace(dataLine))
                        return;

                    if (!autoTermination)
                    {
                        _captureData.Add(dataLine);
                    }
                    else
                    {
                        var currentLineSplit = dataLine.Split(',');
                        double currentTime = Convert.ToDouble(currentLineSplit[11], CultureInfo.InvariantCulture);

                        if(currentTime - startTime >= captureTime)
                        {
                            FinishCapturingAndUpdateUi();
                        }
                        else
                        {
                            _captureData.Add(dataLine);
                        }
                    }
                });
        }

        private void StopCaptureDataFromStream()
        {
            _disposableCaptureStream?.Dispose();

            // explicit hook, only one process
            if (!string.IsNullOrWhiteSpace(SelectedProcessToCapture))
            {
                Task.Run(async () => await WriteCaptureDataToFileAync(SelectedProcessToCapture));
            }
            // auto hook with filtered process list
            else
            {
                var filter = CaptureServiceConfiguration.GetProcessIgnoreList();
                var processList = _captureService.GetAllFilteredProcesses(filter).Distinct();

                foreach (var multiProcess in processList)
                {
                    Task.Run(async () => await WriteCaptureDataToFileAync(multiProcess));
                }
            }
        }

        private void StartCaptureService()
        {
            var serviceConfig = GetRedirectedServiceConfig();
            var startInfo = CaptureServiceConfiguration
                .GetServiceStartInfo(serviceConfig.ConfigParameterToArguments());
            _captureService.StartCaptureService(startInfo);
        }

        private void StopCaptureService() => _captureService.StopCaptureService();

        private string GetOutputFilename(string processName)
        {
            var filename = CaptureServiceConfiguration.GetCaptureFilename(processName);
            string observedDirectory = RecordDirectoryObserver
                .GetInitialObservedDirectory(_appConfiguration.ObservedDirectory);

            return Path.Combine(observedDirectory, filename);
        }

        private async Task WriteCaptureDataToFileAync(string processName)
        {
            var filePath = GetOutputFilename(processName);
            var csv = new StringBuilder();
            csv.AppendLine(CaptureServiceConfiguration.FILE_HEADER);

            //additional data/comment
            string firstLineWithInfos = _captureData.First();
            firstLineWithInfos += "," + HardwareInfo.GetProcessorName();
            firstLineWithInfos += "," + HardwareInfo.GetGraphicCardName();
            firstLineWithInfos += "," + HardwareInfo.GetMotherboardName();
            string[] currentLineSplit = firstLineWithInfos.Split(',');
            firstLineWithInfos += "," + "API: " + currentLineSplit[3];

            // normalize time
            currentLineSplit = firstLineWithInfos.Split(',');
            var timeStart = currentLineSplit[11];
            double normalizedTime = Convert.ToDouble(currentLineSplit[11], CultureInfo.InvariantCulture)
                            - Convert.ToDouble(timeStart, CultureInfo.InvariantCulture);
            currentLineSplit[11] = normalizedTime.ToString(CultureInfo.InvariantCulture);

            csv.AppendLine(string.Join(",", currentLineSplit));

            foreach (var dataLine in _captureData.Skip(1))
            {
                int index = dataLine.IndexOf(".exe");
                if (index > 0)
                {
                    var extractedProcessName = dataLine.Substring(0, index);
                    if (extractedProcessName == processName)
                    {
                        currentLineSplit = dataLine.Split(',');

                        // normalize time
                        normalizedTime = Convert.ToDouble(currentLineSplit[11], CultureInfo.InvariantCulture)
                            - Convert.ToDouble(timeStart, CultureInfo.InvariantCulture);

                        // cutting offset
                        int captureTime = Convert.ToInt32(CaptureTimeString);
                        if (captureTime > 0 && normalizedTime > captureTime)
                            break;

                        currentLineSplit[11] = normalizedTime.ToString(CultureInfo.InvariantCulture);

                        csv.AppendLine(string.Join(",", currentLineSplit));
                    }
                }
            }

            using (var sw = new StreamWriter(filePath))
            {
                await sw.WriteAsync(csv.ToString());
            }
        }

        private ICaptureServiceConfiguration GetRedirectedServiceConfig()
        {
            return new PresentMonServiceConfiguration
            {
                RedirectOutputStream = true,
                ExcludeProcesses = CaptureServiceConfiguration.GetProcessIgnoreList().ToList()
            };
        }

        private async Task SetTaskDelay()
        {
            // put some offset here, PresentMon seems to work not that precise 
            await Task.Delay(TimeSpan.FromMilliseconds(PRESICE_OFFSET + 1000 * Convert.ToInt32(CaptureTimeString)));
        }

        private void OnAddToIgonreList()
        {
            if (SelectedProcessToCapture == null)
                return;

            StopCaptureService();
            CaptureServiceConfiguration.AddProcessToIgnoreList(SelectedProcessToCapture);
            ProcessesToIgnore.Clear();
            ProcessesToIgnore.AddRange(CaptureServiceConfiguration.GetProcessIgnoreList());

            SelectedProcessToCapture = null;
            StartCaptureService();
        }

        private void OnRemoveFromIgnoreList()
        {
            if (SelectedProcessToIgnore == null)
                return;

            StopCaptureService();
            CaptureServiceConfiguration.RemoveProcessFromIgnoreList(SelectedProcessToIgnore);
            ProcessesToIgnore.Clear();
            ProcessesToIgnore.AddRange(CaptureServiceConfiguration.GetProcessIgnoreList());
            StartCaptureService();
        }

        private void OnResetCaptureProcess()
        {
            SelectedProcessToCapture = null;
        }

        private IDisposable GetListUpdatHeartBeat()
        {
            var context = SynchronizationContext.Current;
            return Observable.Generate(0, // dummy initialState
                                        x => true, // dummy condition
                                        x => x, // dummy iterate
                                        x => x, // dummy resultSelector
                                        x => TimeSpan.FromSeconds(2))
                                        .ObserveOn(context)
                                        .SubscribeOn(context)
                                        .Subscribe(x => UpdateProcessToCaptureList());
        }

        private void UpdateProcessToCaptureList()
        {
            var selectedProcessToCapture = SelectedProcessToCapture;
            ProcessesToCapture.Clear();
            var filter = CaptureServiceConfiguration.GetProcessIgnoreList();
            var processList = _captureService.GetAllFilteredProcesses(filter).Distinct();
            ProcessesToCapture.AddRange(processList);

            if (!processList.Contains(selectedProcessToCapture))
                SelectedProcessToCapture = null;
            else
                SelectedProcessToCapture = selectedProcessToCapture;
        }

        private void OnSelectedProcessToCaptureChanged()
        {
            UpdateCaptureStateInfo();
        }

        private void UpdateCaptureStateInfo()
        {
            if (string.IsNullOrWhiteSpace(SelectedProcessToCapture))
            {
                CaptureStateInfo = $"Capturing inactive... select process and press {CaptureHotkeyString} to start.";
                return;
            }

            CaptureStateInfo = $"{SelectedProcessToCapture} selected, press {CaptureHotkeyString} to start capture.";
        }

        private void OnSelectedProcessToIgnoreChanged()
        {
            // throw new NotImplementedException();
        }
    }
}