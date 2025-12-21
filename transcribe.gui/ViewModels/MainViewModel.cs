using System.Collections.ObjectModel;
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

    private SpeakerRecorder? speakerRecorder;
    private MicrophoneRecorder? micRecorder;
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
    public ObservableCollection<string> Models { get; } = new(["TinyEn", "BaseEn", "LargeV3", "LargeV3Turbo"]);
    public ObservableCollection<string> Languages { get; } = new(["Auto", "en", "fr", "es", "de", "it", "pt", "nl", "sv", "no", "da", "fi", "pl", "tr", "ru", "uk", "ar", "he", "hi", "ja", "ko", "zh"]);

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

    private int chunkSizeSeconds = 3;
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

    private string selectedLanguage = "en";
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

    private string selectedModel = "TinyEn";
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

    private string ollamaUrl = DefaultOllamaUrl;
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

    private string ollamaModel = DefaultOllamaModel;
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

    public const string DefaultOllamaUrl = "http://localhost:11434/";
    public const string DefaultOllamaModel = "tx-agent:latest";

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

        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => UpdateElapsed();

        if (!OperatingSystem.IsWindows())
        {
            LogInfo("Live capture is Windows-only; file transcription is available via File → Transcribe WAV…");
        }

        SelectedLanguage = "en";
        SelectedModel = "TinyEn";
        SelectedMicDevice = new DeviceItem(-1, "Disabled");
        RefreshDevices();

        ValidateOutputFolder();
        ValidateOllamaUrl();
        ValidateWavFile();
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
        ChunkSizeSeconds = 3;
        SelectedLanguage = "en";
        SelectedModel = "TinyEn";
        SummarizeWithOllama = false;
        OllamaUrl = DefaultOllamaUrl;
        OllamaModel = DefaultOllamaModel;
        OutputFolder = "";
        ConnectionStatusText = "Not tested";
        IsOllamaConnected = null;
        RaiseCanStartChanged();
        LogInfo("Reset to defaults.");
    }

    private void RefreshDevices()
    {
        SpeakerDevices.Clear();
        MicDevices.Clear();

        // Keep a stable "Disabled" option for mic.
        MicDevices.Add(new DeviceItem(-1, "Disabled"));

        if (OperatingSystem.IsWindows())
        {
            try
            {
                foreach (var device in SpeakerRecorder.GetSpeakers())
                {
                    SpeakerDevices.Add(new DeviceItem(device.Index, device.Name));
                }

                foreach (var device in MicrophoneRecorder.GetMicrophones())
                {
                    MicDevices.Add(new DeviceItem(device.Index, device.Name));
                }
            }
            catch (Exception ex)
            {
                LogError($"Device enumeration failed: {ex.Message}");
            }
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

        if (SelectedModeIndex is 1 or 2 && !OperatingSystem.IsWindows())
        {
            reason = "Live capture is only supported on Windows.";
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
        var value = (SelectedLanguage ?? "Auto").Trim();
        return string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value)
            ? "en"
            : value;
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
            StateText = "Error";
            await StopAsync();
        }
    }

    private async Task StartFileAsync(string language)
    {
        var wav = WavFilePath;
        var outputDir = string.IsNullOrWhiteSpace(OutputFolder) ? Path.GetDirectoryName(wav) : OutputFolder;
        var progress = new Progress<int>(_ => { });

        LogInfo($"Transcribing file: {Path.GetFileName(wav)}");
        await WhisperClient.TranscribeAudioAsync(wav, progress, SelectedModel, language, outputDirectory: outputDir, log: LogInfo);

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

        var modelPath = await WhisperClient.EnsureModelDownloaded(SelectedModel, LogInfo);
        micRecorder = new MicrophoneRecorder(deviceNumber: micIndex, bufferMilliseconds: 50, useWasapiExclusive: false);
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
            emitConsoleOutput: false);

        micRecorder.StartRecording();
        micProcessor.StartProcessing();
    }

    private async Task StartSpeakerAsync(string language)
    {
        var speakerIndex = SelectedSpeakerDevice!.Index;
        var micIndex = SelectedMicDevice?.Index ?? -1;

        transcriptPath = ResolveTranscriptPath();
        LogInfo($"Saving transcript to: {transcriptPath}");

        var modelPath = await WhisperClient.EnsureModelDownloaded(SelectedModel, LogInfo);

        speakerRecorder = new SpeakerRecorder(deviceNumber: speakerIndex);
        merger = new TranscriptionMerger(transcriptPath, TimeSpan.FromSeconds(3));

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
            emitConsoleOutput: false);

        if (micIndex >= 0)
        {
            micRecorder = new MicrophoneRecorder(deviceNumber: micIndex, bufferMilliseconds: 50, useWasapiExclusive: false);
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
                emitConsoleOutput: false);
        }

        speakerRecorder.StartRecording();
        speakerProcessor.StartProcessing();

        if (micRecorder != null && micProcessor != null)
        {
            micRecorder.StartRecording();
            micProcessor.StartProcessing();
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
            return;
        }

        try
        {
            LogInfo("Stopping…");

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
            micProcessor?.Dispose();
            speakerProcessor?.Dispose();
            micRecorder?.Dispose();
            speakerRecorder?.Dispose();
            merger?.Dispose();

            micProcessor = null;
            speakerProcessor = null;
            micRecorder = null;
            speakerRecorder = null;
            merger = null;

            timer.Stop();
            startedAt = null;
            RaisePropertyChanged(nameof(ElapsedText));
            RaisePropertyChanged(nameof(RunStatusLine));

            IsRunning = false;
            StateText = StateText == "Error" ? "Error" : "Idle";
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
            LogInfo("Summarizing with Ollama…");
            var transcript = await File.ReadAllTextAsync(path);
            summaryBuilder.Clear();
            RunOnUi(() => SummaryText = "");
            OutputTabIndex = 1;

            var ollama = new transcribe.OllamaClient(OllamaUrl.Trim(), (OllamaModel ?? DefaultOllamaModel).Trim());
            await ollama.SummarizeTranscript(transcript, AppendSummaryRaw);
        }
        catch (Exception ex)
        {
            LogError($"Summarization error: {ex.Message}");
        }
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
            ConnectionStatusText = "Testing…";
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

        var outputDir = string.IsNullOrWhiteSpace(OutputFolder) ? Path.GetDirectoryName(wav) : OutputFolder;
        var language = ResolveLanguage();
        var progress = new Progress<int>(_ => { });

        try
        {
            LogInfo($"Transcribing file: {Path.GetFileName(wav)}");
            await WhisperClient.TranscribeAudioAsync(wav, progress, SelectedModel, language, outputDirectory: outputDir, log: LogInfo);

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
