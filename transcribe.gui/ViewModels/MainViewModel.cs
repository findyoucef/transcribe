using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Avalonia.Threading;
using Transcribe;
using Transcribe.Gui.Commands;
using Transcribe.Gui.Models;
using Transcribe.Gui.Services;

namespace Transcribe.Gui.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IWindowServices services;
    private readonly DispatcherTimer timer;
    private readonly StringBuilder transcriptBuilder = new();
    private readonly StringBuilder summaryBuilder = new();

    private IAudioRecorder? speakerRecorder;
    private IAudioRecorder? micRecorder;
    private WhisperStreamProcessor? speakerProcessor;
    private WhisperStreamProcessor? micProcessor;
    private TranscriptionMerger? merger;

    private DateTimeOffset? startedAt;
    private string? transcriptPath;

    private int selectedModeIndex;
    public int SelectedModeIndex
    {
        get => selectedModeIndex;
        set
        {
            if (SetProperty(ref selectedModeIndex, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    public bool IsWindows => OperatingSystem.IsWindows();
    public bool IsMacOS => OperatingSystem.IsMacOS();

    private string wavFilePath = "";
    public string WavFilePath
    {
        get => wavFilePath;
        set
        {
            if (SetProperty(ref wavFilePath, value))
            {
                ValidateWavFile();
                RaiseCanStartChanged();
            }
        }
    }

    private bool isWavFileValid = true;
    public bool IsWavFileValid
    {
        get => isWavFileValid;
        private set => SetProperty(ref isWavFileValid, value);
    }

    private string? wavFileValidationMessage;
    public string? WavFileValidationMessage
    {
        get => wavFileValidationMessage;
        private set => SetProperty(ref wavFileValidationMessage, value);
    }

    public ObservableCollection<DeviceItem> SpeakerDevices { get; } = new();
    public ObservableCollection<DeviceItem> MicDevices { get; } = new();
    public ObservableCollection<string> Models { get; } = new(TranscribeDefaults.SupportedModels);
    public ObservableCollection<string> Languages { get; } = new(TranscribeDefaults.LanguageOptions);

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    private DeviceItem? selectedSpeakerDevice;
    public DeviceItem? SelectedSpeakerDevice
    {
        get => selectedSpeakerDevice;
        set
        {
            if (SetProperty(ref selectedSpeakerDevice, value))
            {
                RaisePropertyChanged(nameof(DeviceSummaryTooltip));
                RaiseCanStartChanged();
            }
        }
    }

    private DeviceItem? selectedMicDevice;
    public DeviceItem? SelectedMicDevice
    {
        get => selectedMicDevice;
        set
        {
            if (SetProperty(ref selectedMicDevice, value))
            {
                RaisePropertyChanged(nameof(DeviceSummaryTooltip));
            }
        }
    }

    private string outputFolder = "";
    public string OutputFolder
    {
        get => outputFolder;
        set
        {
            if (SetProperty(ref outputFolder, value))
            {
                ValidateOutputFolder();
                RaisePropertyChanged(nameof(HasOutputFolder));
                RaiseCanStartChanged();
            }
        }
    }

    private bool isOutputFolderValid = true;
    public bool IsOutputFolderValid
    {
        get => isOutputFolderValid;
        private set => SetProperty(ref isOutputFolderValid, value);
    }

    private string? outputFolderValidationMessage;
    public string? OutputFolderValidationMessage
    {
        get => outputFolderValidationMessage;
        private set => SetProperty(ref outputFolderValidationMessage, value);
    }

    private int chunkSizeSeconds = TranscribeDefaults.DefaultChunkSeconds;
    public int ChunkSizeSeconds
    {
        get => chunkSizeSeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 60);
            if (SetProperty(ref chunkSizeSeconds, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private string selectedLanguage = TranscribeDefaults.DefaultLanguage;
    public string SelectedLanguage
    {
        get => selectedLanguage;
        set
        {
            if (SetProperty(ref selectedLanguage, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private string selectedModel = TranscribeDefaults.DefaultModel;
    public string SelectedModel
    {
        get => selectedModel;
        set
        {
            if (SetProperty(ref selectedModel, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private bool enableDiarization;
    public bool EnableDiarization
    {
        get => enableDiarization;
        set
        {
            if (SetProperty(ref enableDiarization, value))
            {
                if (!enableDiarization && DiarizationAdvanced)
                {
                    DiarizationAdvanced = false;
                }
                RaisePropertyChanged(nameof(ShowDiarizationSettings));
                RaiseCanStartChanged();
            }
        }
    }

    private bool diarizationAdvanced;
    public bool DiarizationAdvanced
    {
        get => diarizationAdvanced;
        set
        {
            if (SetProperty(ref diarizationAdvanced, value))
            {
                if (diarizationAdvanced && !EnableDiarization)
                {
                    EnableDiarization = true;
                }
                RaisePropertyChanged(nameof(ShowDiarizationSettings));
                RaiseCanStartChanged();
            }
        }
    }

    public bool ShowDiarizationSettings => EnableDiarization && DiarizationAdvanced;

    private string diarizationModelPath = "";
    public string DiarizationModelPath
    {
        get => diarizationModelPath;
        set
        {
            if (SetProperty(ref diarizationModelPath, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private string diarizationModelUrl = TranscribeDefaults.DefaultDiarizationModelUrl;
    public string DiarizationModelUrl
    {
        get => diarizationModelUrl;
        set
        {
            if (SetProperty(ref diarizationModelUrl, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private string diarizationConfigPath = "";
    public string DiarizationConfigPath
    {
        get => diarizationConfigPath;
        set
        {
            if (SetProperty(ref diarizationConfigPath, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationSimilarityThreshold = TranscribeDefaults.DefaultDiarizationSimilarityThreshold;
    public double DiarizationSimilarityThreshold
    {
        get => diarizationSimilarityThreshold;
        set
        {
            var clamped = Math.Clamp(value, 0.3, 0.95);
            if (SetProperty(ref diarizationSimilarityThreshold, clamped))
            {
                if (DiarizationInstantSwitchSimilarity < diarizationSimilarityThreshold)
                {
                    DiarizationInstantSwitchSimilarity = diarizationSimilarityThreshold;
                }
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationFallbackSimilarityThreshold = TranscribeDefaults.DefaultDiarizationFallbackSimilarity;
    public double DiarizationFallbackSimilarityThreshold
    {
        get => diarizationFallbackSimilarityThreshold;
        set
        {
            var clamped = Math.Clamp(value, 0.3, 0.9);
            if (SetProperty(ref diarizationFallbackSimilarityThreshold, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationActiveMatchMargin = TranscribeDefaults.DefaultDiarizationActiveMatchMargin;
    public double DiarizationActiveMatchMargin
    {
        get => diarizationActiveMatchMargin;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 0.3);
            if (SetProperty(ref diarizationActiveMatchMargin, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationInstantSwitchSimilarity = TranscribeDefaults.DefaultDiarizationInstantSwitchSimilarity;
    public double DiarizationInstantSwitchSimilarity
    {
        get => diarizationInstantSwitchSimilarity;
        set
        {
            var min = DiarizationSimilarityThreshold;
            var clamped = Math.Clamp(value, min, 0.95);
            if (SetProperty(ref diarizationInstantSwitchSimilarity, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationEnergyThreshold = TranscribeDefaults.DefaultDiarizationEnergyThreshold;
    public double DiarizationEnergyThreshold
    {
        get => diarizationEnergyThreshold;
        set
        {
            var clamped = Math.Clamp(value, 0.001, 0.05);
            if (SetProperty(ref diarizationEnergyThreshold, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private int diarizationMinSpeechMs = TranscribeDefaults.DefaultDiarizationMinSpeechMs;
    public int DiarizationMinSpeechMs
    {
        get => diarizationMinSpeechMs;
        set
        {
            var clamped = Math.Clamp(value, 100, 5000);
            if (SetProperty(ref diarizationMinSpeechMs, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private int diarizationMinSilenceMs = TranscribeDefaults.DefaultDiarizationMinSilenceMs;
    public int DiarizationMinSilenceMs
    {
        get => diarizationMinSilenceMs;
        set
        {
            var clamped = Math.Clamp(value, 100, 5000);
            if (SetProperty(ref diarizationMinSilenceMs, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private int diarizationMaxSegmentSeconds = TranscribeDefaults.DefaultDiarizationMaxSegmentSeconds;
    public int DiarizationMaxSegmentSeconds
    {
        get => diarizationMaxSegmentSeconds;
        set
        {
            var clamped = Math.Clamp(value, 2, 60);
            if (SetProperty(ref diarizationMaxSegmentSeconds, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationWindowSeconds = TranscribeDefaults.DefaultDiarizationWindowSeconds;
    public double DiarizationWindowSeconds
    {
        get => diarizationWindowSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 5.0);
            if (SetProperty(ref diarizationWindowSeconds, clamped))
            {
                if (DiarizationWindowHopSeconds > diarizationWindowSeconds)
                {
                    DiarizationWindowHopSeconds = diarizationWindowSeconds;
                }
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationWindowHopSeconds = TranscribeDefaults.DefaultDiarizationWindowHopSeconds;
    public double DiarizationWindowHopSeconds
    {
        get => diarizationWindowHopSeconds;
        set
        {
            var clamped = Math.Clamp(value, 0.1, DiarizationWindowSeconds);
            if (SetProperty(ref diarizationWindowHopSeconds, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private int diarizationMinSpeakerWindows = TranscribeDefaults.DefaultDiarizationMinSpeakerWindows;
    public int DiarizationMinSpeakerWindows
    {
        get => diarizationMinSpeakerWindows;
        set
        {
            var clamped = Math.Clamp(value, 1, 20);
            if (SetProperty(ref diarizationMinSpeakerWindows, clamped))
            {
                if (DiarizationMinNewSpeakerWindows < diarizationMinSpeakerWindows)
                {
                    DiarizationMinNewSpeakerWindows = diarizationMinSpeakerWindows;
                }
                RaiseCanStartChanged();
            }
        }
    }

    private int diarizationMinNewSpeakerWindows = TranscribeDefaults.DefaultDiarizationMinNewSpeakerWindows;
    public int DiarizationMinNewSpeakerWindows
    {
        get => diarizationMinNewSpeakerWindows;
        set
        {
            var clamped = Math.Clamp(value, DiarizationMinSpeakerWindows, 20);
            if (SetProperty(ref diarizationMinNewSpeakerWindows, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationOverlapMinSimilarity = TranscribeDefaults.DefaultDiarizationOverlapMinSimilarity;
    public double DiarizationOverlapMinSimilarity
    {
        get => diarizationOverlapMinSimilarity;
        set
        {
            var clamped = Math.Clamp(value, 0.3, 0.95);
            if (SetProperty(ref diarizationOverlapMinSimilarity, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private double diarizationOverlapMaxGap = TranscribeDefaults.DefaultDiarizationOverlapMaxGap;
    public double DiarizationOverlapMaxGap
    {
        get => diarizationOverlapMaxGap;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 0.3);
            if (SetProperty(ref diarizationOverlapMaxGap, clamped))
            {
                RaiseCanStartChanged();
            }
        }
    }

    private bool diarizationLogScores;
    public bool DiarizationLogScores
    {
        get => diarizationLogScores;
        set
        {
            if (SetProperty(ref diarizationLogScores, value))
            {
                RaiseCanStartChanged();
            }
        }
    }

    public string DefaultDiarizationModelPath => DiarizationModelLoader.GetDefaultModelPath();
    public string DefaultDiarizationConfigPath => DiarizationModelLoader.GetDefaultConfigPath();

    private bool summarizeWithOllama;
    public bool SummarizeWithOllama
    {
        get => summarizeWithOllama;
        set
        {
            if (SetProperty(ref summarizeWithOllama, value))
            {
                ValidateOllamaUrl();
                RaisePropertyChanged(nameof(ShowOllamaSettings));
                RaisePropertyChanged(nameof(ShowSummaryTab));
                RaisePropertyChanged(nameof(LogTabIndex));
                RaisePropertyChanged(nameof(IsLogTabSelected));
                RaiseCanStartChanged();
            }
        }
    }

    public bool ShowOllamaSettings => SummarizeWithOllama;
    public bool ShowSummaryTab => SummarizeWithOllama;
    public int LogTabIndex => ShowSummaryTab ? 2 : 1;
    public bool IsLogTabSelected => OutputTabIndex == LogTabIndex;

    private string ollamaUrl = TranscribeDefaults.DefaultOllamaUrl;
    public string OllamaUrl
    {
        get => ollamaUrl;
        set
        {
            if (SetProperty(ref ollamaUrl, value))
            {
                ValidateOllamaUrl();
                RaiseCanStartChanged();
            }
        }
    }

    private string ollamaModel = TranscribeDefaults.DefaultOllamaModel;
    public string OllamaModel
    {
        get => ollamaModel;
        set => SetProperty(ref ollamaModel, value);
    }

    private bool isOllamaUrlValid = true;
    public bool IsOllamaUrlValid
    {
        get => isOllamaUrlValid;
        private set => SetProperty(ref isOllamaUrlValid, value);
    }

    private string? ollamaUrlValidationMessage;
    public string? OllamaUrlValidationMessage
    {
        get => ollamaUrlValidationMessage;
        private set => SetProperty(ref ollamaUrlValidationMessage, value);
    }

    private string connectionStatusText = "Not tested";
    public string ConnectionStatusText
    {
        get => connectionStatusText;
        private set => SetProperty(ref connectionStatusText, value);
    }

    private bool? isOllamaConnected;
    public bool? IsOllamaConnected
    {
        get => isOllamaConnected;
        private set => SetProperty(ref isOllamaConnected, value);
    }

    private bool isRunning;
    public bool IsRunning
    {
        get => isRunning;
        private set
        {
            if (SetProperty(ref isRunning, value))
            {
                RaiseCanStartChanged();
                ((RelayCommand)StopCommand).RaiseCanExecuteChanged();
            }
        }
    }

    private bool isFileTranscribing;
    public bool IsFileTranscribing
    {
        get => isFileTranscribing;
        private set => SetProperty(ref isFileTranscribing, value);
    }

    private int fileProgressPercent;
    public int FileProgressPercent
    {
        get => fileProgressPercent;
        private set => SetProperty(ref fileProgressPercent, value);
    }

    private string stateText = "Idle";
    public string StateText
    {
        get => stateText;
        private set
        {
            if (SetProperty(ref stateText, value))
            {
                RaisePropertyChanged(nameof(RunStatusLine));
            }
        }
    }

    public string ElapsedText
    {
        get
        {
            if (!IsRunning || startedAt == null)
            {
                return "";
            }

            var elapsed = DateTimeOffset.Now - startedAt.Value;
            return elapsed.ToString(@"hh\:mm\:ss");
        }
    }

    public string RunStatusLine
    {
        get
        {
            var elapsed = ElapsedText;
            return string.IsNullOrWhiteSpace(elapsed)
                ? $"State: {StateText}"
                : $"State: {StateText}  Elapsed: {elapsed}";
        }
    }

    private int outputTabIndex;
    public int OutputTabIndex
    {
        get => outputTabIndex;
        set
        {
            if (SetProperty(ref outputTabIndex, value))
            {
                RaisePropertyChanged(nameof(IsLogTabSelected));
            }
        }
    }

    private string transcriptText = "";
    public string TranscriptText
    {
        get => transcriptText;
        private set => SetProperty(ref transcriptText, value);
    }

    private string summaryText = "";
    public string SummaryText
    {
        get => summaryText;
        private set => SetProperty(ref summaryText, value);
    }

    public string DeviceSummaryTooltip
    {
        get
        {
            var speaker = SelectedSpeakerDevice?.Name ?? "(none)";
            var mic = SelectedMicDevice?.Index is >= 0 ? SelectedMicDevice!.Name : "Disabled";
            return $"Speaker: {speaker}\nMic: {mic}";
        }
    }

    public bool CanStart => ComputeCanStart(out _);
    public string? CannotStartReason => ComputeCanStart(out var reason) ? null : reason;
    public bool ShowCannotStartReason => !CanStart && !string.IsNullOrWhiteSpace(CannotStartReason);

    public bool HasOutputFolder => !string.IsNullOrWhiteSpace(OutputFolder);

    public RelayCommand RefreshDevicesCommand { get; }
    public RelayCommand BrowseOutputFolderCommand { get; }
    public RelayCommand BrowseWavFileCommand { get; }
    public RelayCommand TestOllamaConnectionCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CopyOutputCommand { get; }
    public RelayCommand SaveOutputCommand { get; }
    public RelayCommand TranscribeWavCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand<string> InstallBlackHoleCommand { get; }

    public MainViewModel(IWindowServices services)
    {
        this.services = services;

        RefreshDevicesCommand = new RelayCommand(RefreshDevices);
        BrowseOutputFolderCommand = new RelayCommand(async () => await BrowseOutputFolderAsync());
        BrowseWavFileCommand = new RelayCommand(async () => await BrowseWavFileAsync());
        TestOllamaConnectionCommand = new RelayCommand(async () => await TestOllamaConnectionAsync(), () => ShowOllamaSettings);
        StartCommand = new RelayCommand(async () => await StartAsync(), () => CanStart);
        StopCommand = new RelayCommand(async () => await StopAsync(), () => IsRunning);
        ResetDefaultsCommand = new RelayCommand(ResetDefaults);
        ClearLogCommand = new RelayCommand(() => RunOnUi(() => LogEntries.Clear()));
        CopyOutputCommand = new RelayCommand(async () => await CopyCurrentAsync());
        SaveOutputCommand = new RelayCommand(async () => await SaveCurrentAsync());
        TranscribeWavCommand = new RelayCommand(async () => await TranscribeWavAsync());
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        InstallBlackHoleCommand = new RelayCommand<string>(InstallBlackHole);

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateElapsed();

        SelectedLanguage = TranscribeDefaults.DefaultLanguage;
        SelectedModel = TranscribeDefaults.DefaultModel;
        SelectedMicDevice = new DeviceItem(-1, "Disabled");
        RefreshDevices();

        ValidateOutputFolder();
        ValidateOllamaUrl();
        ValidateWavFile();
    }

    private void InstallBlackHole(string? mode)
    {
        if (!IsMacOS) return;

        try
        {
            if (string.Equals(mode, "brew", StringComparison.OrdinalIgnoreCase))
            {
                LogInfo("Installing via Homebrew...");
                Process.Start("brew", "install --cask blackhole-2ch");
            }
            else
            {
                // Default to PKG
                var appDir = AppContext.BaseDirectory;
                // Check multiple depths for development vs published structure
                var pkgPath = Path.Combine(appDir, "installers", "BlackHole2ch-0.6.1.pkg");
                
                if (!File.Exists(pkgPath))
                {
                    // Fallback for some build configurations
                    pkgPath = Path.Combine(appDir, "..", "..", "..", "..", "installers", "BlackHole2ch-0.6.1.pkg");
                }

                if (File.Exists(pkgPath))
                {
                    LogInfo($"Opening installer: {pkgPath}");
                    Process.Start("open", pkgPath);
                }
                else
                {
                    LogWarn($"Installer not found at: {pkgPath}. Please use Homebrew or download manually.");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Installation error: {ex.Message}");
        }
    }

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    private void SetFileProgress(int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        RunOnUi(() => FileProgressPercent = clamped);
    }

    private void SetFileTranscribing(bool isTranscribing)
    {
        RunOnUi(() => IsFileTranscribing = isTranscribing);
    }

    private void RaiseCanStartChanged()
    {
        RaisePropertyChanged(nameof(CanStart));
        RaisePropertyChanged(nameof(CannotStartReason));
        StartCommand.RaiseCanExecuteChanged();
        TestOllamaConnectionCommand.RaiseCanExecuteChanged();
    }

    private void ResetDefaults()
    {
        WavFilePath = "";
        ChunkSizeSeconds = TranscribeDefaults.DefaultChunkSeconds;
        SelectedLanguage = TranscribeDefaults.DefaultLanguage;
        SelectedModel = TranscribeDefaults.DefaultModel;
        EnableDiarization = false;
        DiarizationAdvanced = false;
        DiarizationModelPath = "";
        DiarizationModelUrl = TranscribeDefaults.DefaultDiarizationModelUrl;
        DiarizationConfigPath = "";
        DiarizationSimilarityThreshold = TranscribeDefaults.DefaultDiarizationSimilarityThreshold;
        DiarizationFallbackSimilarityThreshold = TranscribeDefaults.DefaultDiarizationFallbackSimilarity;
        DiarizationActiveMatchMargin = TranscribeDefaults.DefaultDiarizationActiveMatchMargin;
        DiarizationInstantSwitchSimilarity = TranscribeDefaults.DefaultDiarizationInstantSwitchSimilarity;
        DiarizationEnergyThreshold = TranscribeDefaults.DefaultDiarizationEnergyThreshold;
        DiarizationMinSpeechMs = TranscribeDefaults.DefaultDiarizationMinSpeechMs;
        DiarizationMinSilenceMs = TranscribeDefaults.DefaultDiarizationMinSilenceMs;
        DiarizationMaxSegmentSeconds = TranscribeDefaults.DefaultDiarizationMaxSegmentSeconds;
        DiarizationWindowSeconds = TranscribeDefaults.DefaultDiarizationWindowSeconds;
        DiarizationWindowHopSeconds = TranscribeDefaults.DefaultDiarizationWindowHopSeconds;
        DiarizationMinSpeakerWindows = TranscribeDefaults.DefaultDiarizationMinSpeakerWindows;
        DiarizationMinNewSpeakerWindows = TranscribeDefaults.DefaultDiarizationMinNewSpeakerWindows;
        DiarizationOverlapMinSimilarity = TranscribeDefaults.DefaultDiarizationOverlapMinSimilarity;
        DiarizationOverlapMaxGap = TranscribeDefaults.DefaultDiarizationOverlapMaxGap;
        DiarizationLogScores = false;
        SummarizeWithOllama = false;
        OllamaUrl = TranscribeDefaults.DefaultOllamaUrl;
        OllamaModel = TranscribeDefaults.DefaultOllamaModel;
        OutputFolder = "";
        ConnectionStatusText = "Not tested";
        IsOllamaConnected = null;
        FileProgressPercent = 0;
        IsFileTranscribing = false;
        RaiseCanStartChanged();
        LogInfo("Reset to defaults.");
    }

    private void RefreshDevices()
    {
        SpeakerDevices.Clear();
        MicDevices.Clear();

        // Keep a stable "Disabled" option for mic.
        MicDevices.Add(new DeviceItem(-1, "Disabled"));

        try
        {
            foreach (var device in AudioCaptureFactory.GetSpeakers())
            {
                SpeakerDevices.Add(new DeviceItem(device.Index, device.Name));
            }

            foreach (var device in AudioCaptureFactory.GetMicrophones())
            {
                MicDevices.Add(new DeviceItem(device.Index, device.Name));
            }
        }
        catch (Exception ex)
        {
            LogError($"Device enumeration failed: {ex.Message}");
        }

        SelectedSpeakerDevice ??= SpeakerDevices.FirstOrDefault();
        SelectedMicDevice ??= MicDevices.FirstOrDefault();

        RaisePropertyChanged(nameof(DeviceSummaryTooltip));
        RaiseCanStartChanged();
    }

    private async Task BrowseOutputFolderAsync()
    {
        var folder = await services.PickFolderAsync("Select output folder");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            OutputFolder = folder;
        }
    }

    private async Task BrowseWavFileAsync()
    {
        var wav = await services.PickWavFileAsync("Select WAV file");
        if (string.IsNullOrWhiteSpace(wav))
        {
            return;
        }

        WavFilePath = wav;

        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            var dir = Path.GetDirectoryName(wav);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                OutputFolder = dir;
            }
        }
    }

    private void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            return;
        }

        services.OpenFolderInShell(OutputFolder);
    }

    private void ValidateOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            IsOutputFolderValid = true;
            OutputFolderValidationMessage = null;
            RaisePropertyChanged(nameof(HasOutputFolder));
            return;
        }

        try
        {
            Directory.CreateDirectory(OutputFolder);
            var probe = Path.Combine(OutputFolder, $".tx_probe_{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                // ok
            }

            IsOutputFolderValid = true;
            OutputFolderValidationMessage = null;
            RaisePropertyChanged(nameof(HasOutputFolder));
        }
        catch (Exception ex)
        {
            IsOutputFolderValid = false;
            OutputFolderValidationMessage = ex.Message;
            RaisePropertyChanged(nameof(HasOutputFolder));
        }
    }

    private void ValidateWavFile()
    {
        if (string.IsNullOrWhiteSpace(WavFilePath))
        {
            IsWavFileValid = true;
            WavFileValidationMessage = null;
            return;
        }

        if (!File.Exists(WavFilePath))
        {
            IsWavFileValid = false;
            WavFileValidationMessage = "File does not exist.";
            return;
        }

        if (!string.Equals(Path.GetExtension(WavFilePath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            IsWavFileValid = false;
            WavFileValidationMessage = "Select a .wav file.";
            return;
        }

        IsWavFileValid = true;
        WavFileValidationMessage = null;
    }

    private void ValidateOllamaUrl()
    {
        if (!SummarizeWithOllama)
        {
            IsOllamaUrlValid = true;
            OllamaUrlValidationMessage = null;
            return;
        }

        var url = (OllamaUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            IsOllamaUrlValid = false;
            OllamaUrlValidationMessage = "Ollama URL is required.";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            IsOllamaUrlValid = false;
            OllamaUrlValidationMessage = "Invalid URL.";
            return;
        }

        if (parsed.Scheme is not ("http" or "https"))
        {
            IsOllamaUrlValid = false;
            OllamaUrlValidationMessage = "URL must be http or https.";
            return;
        }

        IsOllamaUrlValid = true;
        OllamaUrlValidationMessage = null;
    }

    private bool ComputeCanStart(out string? reason)
    {
        if (IsRunning)
        {
            reason = "Already running.";
            return false;
        }

        // Modes: 0=File, 1=Microphone, 2=Speaker, 3=Devices
        if (SelectedModeIndex == 3)
        {
            reason = "Select a run mode.";
            return false;
        }

        if (SelectedModeIndex == 0)
        {
            if (string.IsNullOrWhiteSpace(WavFilePath))
            {
                reason = "Select a WAV file.";
                return false;
            }
            if (!IsWavFileValid)
            {
                reason = "WAV file is invalid.";
                return false;
            }
        }

        if (SelectedModeIndex == 1)
        {
            if (SelectedMicDevice == null || SelectedMicDevice.Index < 0)
            {
                reason = "Select a microphone device.";
                return false;
            }
        }

        if (SelectedModeIndex == 2 && SelectedSpeakerDevice == null)
        {
            reason = "Select a speaker device.";
            return false;
        }

        if (!IsOutputFolderValid)
        {
            reason = "Output folder is invalid or not writable.";
            return false;
        }

        if (EnableDiarization)
        {
            var modelPath = ResolveDiarizationModelPath();
            var modelUrl = NormalizeOptional(DiarizationModelUrl);
            if (string.IsNullOrWhiteSpace(modelUrl) && !File.Exists(modelPath))
            {
                reason = "Diarization model is missing. Provide a model path or URL.";
                return false;
            }
        }

        if (SummarizeWithOllama && !IsOllamaUrlValid)
        {
            reason = "Fix Ollama URL.";
            return false;
        }

        reason = null;
        return true;
    }

    private string ResolveTranscriptPath()
    {
        var name = $"{Guid.NewGuid():N}.txt";
        if (string.IsNullOrWhiteSpace(OutputFolder))
        {
            return name;
        }

        Directory.CreateDirectory(OutputFolder);
        return Path.Combine(OutputFolder, name);
    }

    private string ResolveLanguage()
    {
        return TranscribeDefaults.NormalizeLanguage(SelectedLanguage);
    }

    private DiarizationOptions BuildDiarizationOptions()
    {
        return new DiarizationOptions
        {
            Enabled = EnableDiarization,
            ModelPath = NormalizeOptional(DiarizationModelPath),
            ModelUrl = NormalizeOptional(DiarizationModelUrl),
            ConfigPath = NormalizeOptional(DiarizationConfigPath),
            SimilarityThreshold = (float)DiarizationSimilarityThreshold,
            FallbackSimilarityThreshold = (float)DiarizationFallbackSimilarityThreshold,
            ActiveMatchMargin = (float)DiarizationActiveMatchMargin,
            InstantSwitchSimilarity = (float)DiarizationInstantSwitchSimilarity,
            EnergyThreshold = (float)DiarizationEnergyThreshold,
            MinSpeechMs = DiarizationMinSpeechMs,
            MinSilenceMs = DiarizationMinSilenceMs,
            MaxSegmentSeconds = DiarizationMaxSegmentSeconds,
            WindowSeconds = (float)DiarizationWindowSeconds,
            WindowHopSeconds = (float)DiarizationWindowHopSeconds,
            MinSpeakerWindows = DiarizationMinSpeakerWindows,
            MinNewSpeakerWindows = DiarizationMinNewSpeakerWindows,
            OverlapMinSimilarity = (float)DiarizationOverlapMinSimilarity,
            OverlapMaxGap = (float)DiarizationOverlapMaxGap,
            LogScores = DiarizationLogScores
        };
    }

    private string ResolveDiarizationModelPath()
    {
        var path = NormalizeOptional(DiarizationModelPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return DiarizationModelLoader.GetDefaultModelPath();
        }

        return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private async Task StartAsync()
    {
        if (!ComputeCanStart(out var reason))
        {
            LogWarn(reason ?? "Cannot start.");
            return;
        }

        RunOnUi(() =>
        {
            transcriptBuilder.Clear();
            summaryBuilder.Clear();
            TranscriptText = "";
            SummaryText = "";
        });
        OutputTabIndex = 0;

        StateText = "Running";
        IsRunning = true;

        startedAt = DateTimeOffset.Now;
        timer.Start();
        UpdateElapsed();

        var language = ResolveLanguage();

        try
        {
            switch (SelectedModeIndex)
            {
                case 0:
                    await StartFileAsync(language);
                    break;
                case 1:
                    await StartMicrophoneAsync(language);
                    break;
                case 2:
                    await StartSpeakerAsync(language);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            LogError($"Start failed: {ex.Message}");
            StateText = $"Error: {ex.Message}";
            OutputTabIndex = LogTabIndex;
            await StopAsync();
        }
    }

    private async Task StartFileAsync(string language)
    {
        var wav = WavFilePath;
        var outputDir = string.IsNullOrWhiteSpace(OutputFolder) ? Path.GetDirectoryName(wav) : OutputFolder;
        var progress = new Progress<int>(percent => SetFileProgress(percent));
        SetFileProgress(0);
        SetFileTranscribing(true);
        var completed = false;
        var diarizationOptions = BuildDiarizationOptions();

        LogInfo($"Transcribing file: {Path.GetFileName(wav)}");
        try
        {
            await Task.Run(() => WhisperClient.TranscribeAudioAsync(
                wav,
                progress,
                SelectedModel,
                language,
                outputDirectory: outputDir,
                log: LogInfo,
                onSegment: AppendTranscriptLineNoLog,
                diarizationOptions: diarizationOptions));
            completed = true;
        }
        finally
        {
            if (completed)
            {
                SetFileProgress(100);
            }
            SetFileTranscribing(false);
        }

        var txtPath = Path.Combine(outputDir ?? Directory.GetCurrentDirectory(), $"{Path.GetFileNameWithoutExtension(wav)}.txt");
        transcriptPath = txtPath;

        if (File.Exists(txtPath))
        {
            transcriptBuilder.Clear();
            transcriptBuilder.Append(await File.ReadAllTextAsync(txtPath));
            RunOnUi(() => TranscriptText = transcriptBuilder.ToString());
            OutputTabIndex = 0;
            LogInfo($"Transcript: {txtPath}");
        }

        // Finish immediately (no Stop button needed for file mode)
        StateText = "Idle";
        IsRunning = false;
        timer.Stop();
        startedAt = null;
        RaisePropertyChanged(nameof(ElapsedText));
        RaisePropertyChanged(nameof(RunStatusLine));

        if (SummarizeWithOllama && transcriptPath != null && File.Exists(transcriptPath))
        {
            await SummarizeAsync(transcriptPath);
        }
    }

    private async Task StartMicrophoneAsync(string language)
    {
        var micIndex = SelectedMicDevice?.Index ?? -1;
        transcriptPath = ResolveTranscriptPath();
        LogInfo($"Saving transcript to: {transcriptPath}");

        var diarizationOptions = BuildDiarizationOptions();
        DiarizationResources? diarizationResources = null;
        if (diarizationOptions.Enabled)
        {
            diarizationResources = await DiarizationModelLoader.EnsureResourcesAsync(diarizationOptions, LogInfo);
        }

        var modelPath = await WhisperClient.EnsureModelDownloaded(SelectedModel, LogInfo);
        DiarizationPipeline? diarization = null;
        try
        {
            if (diarizationResources != null)
            {
                var diarizationModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, LogInfo);
                diarization = new DiarizationPipeline(diarizationModel, diarizationOptions, LogInfo);
            }

            micRecorder = AudioCaptureFactory.CreateMicrophoneRecorder(deviceIndex: micIndex);
            micProcessor = new WhisperStreamProcessor(
                modelPath,
                language,
                micRecorder,
                chunkSizeSeconds: ChunkSizeSeconds,
                sourceLabel: "MIC",
                showLevels: false,
                transcriptFilePath: transcriptPath,
                onText: AppendTranscriptLine,
                onStatus: LogInfo,
                emitConsoleOutput: false,
                diarization: diarization);

            micRecorder.StartRecording();
            micProcessor.StartProcessing();

            diarization = null;
        }
        finally
        {
            diarization?.Dispose();
        }
    }

    private async Task StartSpeakerAsync(string language)
    {
        var speakerIndex = SelectedSpeakerDevice!.Index;
        var micIndex = SelectedMicDevice?.Index ?? -1;

        transcriptPath = ResolveTranscriptPath();
        LogInfo($"Saving transcript to: {transcriptPath}");

        var diarizationOptions = BuildDiarizationOptions();
        DiarizationResources? diarizationResources = null;
        if (diarizationOptions.Enabled)
        {
            diarizationResources = await DiarizationModelLoader.EnsureResourcesAsync(diarizationOptions, LogInfo);
        }

        var modelPath = await WhisperClient.EnsureModelDownloaded(SelectedModel, LogInfo);

        speakerRecorder = AudioCaptureFactory.CreateSpeakerRecorder(deviceIndex: speakerIndex);
        merger = new TranscriptionMerger(transcriptPath, TimeSpan.FromSeconds(3));

        DiarizationPipeline? speakerDiarization = null;
        DiarizationPipeline? micDiarization = null;
        try
        {
            if (diarizationResources != null)
            {
                var diarizationModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, LogInfo);
                speakerDiarization = new DiarizationPipeline(diarizationModel, diarizationOptions, LogInfo);
            }

            speakerProcessor = new WhisperStreamProcessor(
                modelPath,
                language,
                speakerRecorder,
                chunkSizeSeconds: ChunkSizeSeconds,
                sourceLabel: "SPK",
                showLevels: false,
                transcriptFilePath: transcriptPath,
                merger: merger,
                onText: AppendTranscriptLine,
                onStatus: LogInfo,
                emitConsoleOutput: false,
                diarization: speakerDiarization);

            if (micIndex >= 0)
            {
                micRecorder = AudioCaptureFactory.CreateMicrophoneRecorder(deviceIndex: micIndex);
                if (diarizationResources != null)
                {
                    var micModel = new SpeakerEmbeddingModel(diarizationResources.ModelPath, diarizationResources.Config, LogInfo);
                    micDiarization = new DiarizationPipeline(micModel, diarizationOptions, LogInfo);
                }

                micProcessor = new WhisperStreamProcessor(
                    modelPath,
                    language,
                    micRecorder,
                    chunkSizeSeconds: ChunkSizeSeconds,
                    sourceLabel: "MIC",
                    showLevels: false,
                    transcriptFilePath: transcriptPath,
                    merger: merger,
                    onText: AppendTranscriptLine,
                    onStatus: LogInfo,
                    emitConsoleOutput: false,
                    diarization: micDiarization);
            }

            speakerRecorder.StartRecording();
            speakerProcessor.StartProcessing();

            if (micRecorder != null && micProcessor != null)
            {
                micRecorder.StartRecording();
                micProcessor.StartProcessing();
            }

            speakerDiarization = null;
            micDiarization = null;
        }
        finally
        {
            micDiarization?.Dispose();
            speakerDiarization?.Dispose();
        }
    }

        private async Task StopAsync()
        {
            if (!IsRunning)
            {
                return;
            }

        // Nothing to stop in file mode; it completes synchronously.
        if (SelectedModeIndex == 0)
        {
            timer.Stop();
            startedAt = null;
            RaisePropertyChanged(nameof(ElapsedText));
            RaisePropertyChanged(nameof(RunStatusLine));

            IsRunning = false;
            var isError = StateText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || string.Equals(StateText, "Error", StringComparison.OrdinalIgnoreCase);
            StateText = isError ? StateText : "Idle";
            return;
        }

        try
        {
            LogInfo("Stopping...");

            speakerRecorder?.StopRecording();
            micRecorder?.StopRecording();

            if (speakerProcessor != null)
            {
                await speakerProcessor.StopProcessing();
            }

            if (micProcessor != null)
            {
                await micProcessor.StopProcessing();
            }
        }
        catch (Exception ex)
        {
            LogError($"Stop error: {ex.Message}");
        }
        finally
        {
            merger?.Dispose();
            merger = null;

            micProcessor?.Dispose();
            speakerProcessor?.Dispose();
            micRecorder?.Dispose();
            speakerRecorder?.Dispose();

            micProcessor = null;
            speakerProcessor = null;
            micRecorder = null;
            speakerRecorder = null;

            timer.Stop();
            startedAt = null;
            RaisePropertyChanged(nameof(ElapsedText));
            RaisePropertyChanged(nameof(RunStatusLine));

            IsRunning = false;
            var isError = StateText.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || string.Equals(StateText, "Error", StringComparison.OrdinalIgnoreCase);
            StateText = isError ? StateText : "Idle";
        }

        if (SummarizeWithOllama && transcriptPath != null && File.Exists(transcriptPath))
        {
            await SummarizeAsync(transcriptPath);
        }
    }

    private async Task SummarizeAsync(string path)
    {
        ValidateOllamaUrl();
        if (!IsOllamaUrlValid)
        {
            LogWarn(OllamaUrlValidationMessage ?? "Invalid Ollama URL.");
            return;
        }

        try
        {
            LogInfo("Summarizing with Ollama...");
            var transcript = await File.ReadAllTextAsync(path);
            summaryBuilder.Clear();
            RunOnUi(() => SummaryText = "");
            OutputTabIndex = 1;

            var model = string.IsNullOrWhiteSpace(OllamaModel)
                ? TranscribeDefaults.DefaultOllamaModel
                : OllamaModel.Trim();
            var ollama = new transcribe.OllamaClient(OllamaUrl.Trim(), model);
            await ollama.SummarizeTranscript(transcript, AppendSummaryRaw);
        }
        catch (Exception ex)
        {
            LogError($"Summarization error: {ex.Message}");
        }
    }

    private void AppendTranscriptLineNoLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        RunOnUi(() =>
        {
            transcriptBuilder.AppendLine(line);
            TranscriptText = transcriptBuilder.ToString();
        });
    }

    private void AppendTranscriptLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        RunOnUi(() =>
        {
            transcriptBuilder.AppendLine(line);
            TranscriptText = transcriptBuilder.ToString();
            AddLog(LogLevel.Info, line);
        });
    }

    private void AppendSummaryRaw(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        RunOnUi(() =>
        {
            summaryBuilder.Append(text);
            SummaryText = summaryBuilder.ToString();
        });
    }

    private void UpdateElapsed()
    {
        RaisePropertyChanged(nameof(ElapsedText));
        RaisePropertyChanged(nameof(RunStatusLine));
    }

    private async Task TestOllamaConnectionAsync()
    {
        ValidateOllamaUrl();
        if (!IsOllamaUrlValid)
        {
            ConnectionStatusText = OllamaUrlValidationMessage ?? "Invalid URL.";
            IsOllamaConnected = false;
            RaiseCanStartChanged();
            return;
        }

        try
        {
            ConnectionStatusText = "Testing...";
            IsOllamaConnected = null;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var baseUri = new Uri(OllamaUrl.Trim().TrimEnd('/') + "/");
            var resp = await http.GetAsync(new Uri(baseUri, "api/tags"));
            if (!resp.IsSuccessStatusCode)
            {
                ConnectionStatusText = $"Failed: HTTP {(int)resp.StatusCode}";
                IsOllamaConnected = false;
                return;
            }

            ConnectionStatusText = "Connected";
            IsOllamaConnected = true;
        }
        catch (Exception ex)
        {
            ConnectionStatusText = $"Failed: {ex.Message}";
            IsOllamaConnected = false;
        }
    }

    private async Task CopyCurrentAsync()
    {
        var context = OutputTabIndex switch
        {
            0 => "Transcript",
            1 => "Summary",
            _ => "Log",
        };

        var text = services.GetSelectionOrAllText(context);
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await services.CopyToClipboardAsync(text);
        LogInfo($"Copied {context}.");
    }

    private async Task SaveCurrentAsync()
    {
        var suggestedName = OutputTabIndex switch
        {
            0 => "transcript.txt",
            1 => "summary.txt",
            _ => "log.txt",
        };

        var path = await services.SaveTextFileAsync(suggestedName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var context = OutputTabIndex switch
        {
            0 => "Transcript",
            1 => "Summary",
            _ => "Log",
        };

        var text = services.GetSelectionOrAllText(context) ?? string.Empty;
        await File.WriteAllTextAsync(path, text);
        LogInfo($"Saved {context} to: {path}");
    }

    private async Task TranscribeWavAsync()
    {
        var wav = await services.PickWavFileAsync("Select WAV file");
        if (string.IsNullOrWhiteSpace(wav))
        {
            return;
        }

        TranscriptText = "";
        SummaryText = "";
        OutputTabIndex = 0;
        transcriptBuilder.Clear();
        summaryBuilder.Clear();

        var outputDir = string.IsNullOrWhiteSpace(OutputFolder) ? Path.GetDirectoryName(wav) : OutputFolder;
        var language = ResolveLanguage();
        var progress = new Progress<int>(percent => SetFileProgress(percent));
        SetFileProgress(0);
        SetFileTranscribing(true);
        var completed = false;
        var diarizationOptions = BuildDiarizationOptions();

        try
        {
            LogInfo($"Transcribing file: {Path.GetFileName(wav)}");
            await Task.Run(() => WhisperClient.TranscribeAudioAsync(
                wav,
                progress,
                SelectedModel,
                language,
                outputDirectory: outputDir,
                log: LogInfo,
                onSegment: AppendTranscriptLineNoLog,
                diarizationOptions: diarizationOptions));
            completed = true;

            var txtPath = Path.Combine(outputDir ?? Directory.GetCurrentDirectory(), $"{Path.GetFileNameWithoutExtension(wav)}.txt");
            if (File.Exists(txtPath))
            {
                TranscriptText = await File.ReadAllTextAsync(txtPath);
                transcriptPath = txtPath;
                LogInfo($"Transcript: {txtPath}");
            }
        }
        catch (Exception ex)
        {
            LogError($"File transcription error: {ex.Message}");
        }
        finally
        {
            if (completed)
            {
                SetFileProgress(100);
            }
            SetFileTranscribing(false);
        }
    }

    private void LogInfo(string message) => AddLog(LogLevel.Info, message);
    private void LogWarn(string message) => AddLog(LogLevel.Warn, message);
    private void LogError(string message) => AddLog(LogLevel.Error, message);

    private void AddLog(LogLevel level, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        RunOnUi(() =>
        {
            // Keep logs bounded to avoid unbounded growth.
            const int max = 2000;
            if (LogEntries.Count >= max)
            {
                LogEntries.RemoveAt(0);
            }

            LogEntries.Add(new LogEntry(DateTimeOffset.Now, level, message));
        });
    }
}

