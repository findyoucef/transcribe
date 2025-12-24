using System.Globalization;
using Whisper.net;

namespace Transcribe
{
    public class WhisperStreamProcessor : IDisposable
    {
        private readonly WhisperFactory whisperFactory;
        private readonly WhisperProcessor processor;
        private readonly CancellationTokenSource cancellationTokenSource;
        private Task? processingTask;
        private Task? visualizationTask;
        private readonly List<byte> audioBuffer;
        private int chunkSizeBytes;
        private readonly int chunkSizeSeconds;
        private readonly IAudioRecorder recorder;
        private readonly StreamWriter transcriptWriter;
        private readonly string transcriptFileName;
        private readonly string? sourceLabel;
        private readonly bool showLevels;
        private readonly TranscriptionMerger? merger;
        private readonly Action<string>? onText;
        private readonly Action<string>? onStatus;
        private readonly bool emitConsoleOutput;
        private readonly DiarizationPipeline? diarization;
        private TimeSpan audioCursor;
        private int bytesPerSecond;
        private const double DiarizationChunkSnapSeconds = 0.75;

        private readonly List<float> diarizationBuffer = new();
        private TimeSpan diarizationBufferStart = TimeSpan.Zero;
        private readonly List<PendingSegment> pendingSegments = new();
        private readonly List<WindowDiarization> diarizationWindows = new();
        private OnlineClusterer? globalClusterer;
        private int diarizationSampleRate;
        private TimeSpan diarizationDelay = TimeSpan.Zero;
        private TimeSpan diarizationWindowDuration = TimeSpan.Zero;
        private TimeSpan nextDiarizationWindowStart = TimeSpan.Zero;
        private string? lastEmittedSpeaker;
        private bool diarizationFinalized;

        private sealed record PendingSegment(TimeSpan Start, TimeSpan End, string Text, string Source);
        private sealed record WindowDiarization(TimeSpan Start, TimeSpan End, DiarizationTimeline Timeline);

        public WhisperStreamProcessor(
            string modelPath,
            string language,
            IAudioRecorder recorder,
            int chunkSizeSeconds = TranscribeDefaults.DefaultChunkSeconds,
            string? sourceLabel = null,
            bool showLevels = true,
            string? transcriptFilePath = null,
            TranscriptionMerger? merger = null,
            Action<string>? onText = null,
            Action<string>? onStatus = null,
            bool emitConsoleOutput = true,
            DiarizationPipeline? diarization = null)
        {
            this.recorder = recorder;
            whisperFactory = WhisperFactory.FromPath(modelPath);
            var resolvedLanguage = TranscribeDefaults.NormalizeLanguage(language);
            processor = whisperFactory.CreateBuilder()
                .WithLanguage(resolvedLanguage)
                .Build();

            this.sourceLabel = sourceLabel;
            this.showLevels = showLevels;
            this.merger = merger;
            this.onText = onText;
            this.onStatus = onStatus;
            this.emitConsoleOutput = emitConsoleOutput;
            this.diarization = diarization;
            this.chunkSizeSeconds = Math.Max(1, chunkSizeSeconds);

            cancellationTokenSource = new CancellationTokenSource();
            audioBuffer = new List<byte>();
            chunkSizeBytes = 0;
            bytesPerSecond = 0;
            audioCursor = TimeSpan.Zero;

            // Use provided transcript path or create a new GUID
            transcriptFileName = string.IsNullOrWhiteSpace(transcriptFilePath) ? $"{Guid.NewGuid():N}.txt" : transcriptFilePath;

            // If merger is not provided, create a private writer
            if (merger == null)
            {
                var fs = new FileStream(transcriptFileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                fs.Seek(0, SeekOrigin.End); // append
                transcriptWriter = new StreamWriter(fs) { AutoFlush = true };
            }
            else
            {
                transcriptWriter = TextWriter.Null as StreamWriter ?? new StreamWriter(Stream.Null);
            }
        }

        public void StartProcessing()
        {
            EnsureChunkSizeInitialized();
            var message = $"Saving transcript to: {transcriptFileName}" + (sourceLabel != null ? $" [{sourceLabel}]" : string.Empty);
            if (emitConsoleOutput)
            {
                Console.WriteLine(message);
                Console.WriteLine();
            }
            else
            {
                onStatus?.Invoke(message);
            }

            audioCursor = TimeSpan.Zero;

            processingTask = Task.Run(() => ProcessAudioLoop());
            if (showLevels)
            {
                visualizationTask = Task.Run(() => AudioVisualizationLoop());
            }
        }

        private void EnsureChunkSizeInitialized()
        {
            var bytesPerSecond = recorder.GetBytesPerSecond();
            if (bytesPerSecond <= 0)
            {
                // Fallback to 16kHz mono 16-bit
                bytesPerSecond = 32000;
            }

            var effectiveSeconds = GetEffectiveChunkSeconds();
            var desired = (int)Math.Round(effectiveSeconds * bytesPerSecond);
            if (chunkSizeBytes != desired)
            {
                chunkSizeBytes = desired;
            }

            this.bytesPerSecond = bytesPerSecond;
        }

        private void EnsureDiarizationInitialized()
        {
            if (diarization == null || diarizationSampleRate > 0)
            {
                return;
            }

            diarizationSampleRate = diarization.SampleRate;
            diarizationDelay = ResolveLiveDiarizationDelay();
            diarizationWindowDuration = diarizationDelay;
            if (diarizationWindowDuration <= TimeSpan.Zero)
            {
                diarizationWindowDuration = ResolveLiveDiarizationDelay();
            }

            var similarity = diarization.Options.SimilarityThreshold;
            var fallback = diarization.Options.FallbackSimilarityThreshold;
            var clusterThreshold = fallback > 0 ? Math.Min(similarity, fallback) : similarity;
            globalClusterer = new OnlineClusterer(clusterThreshold);
        }

        private async Task AudioVisualizationLoop()
        {
            while (!cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var level = recorder.GetCurrentAudioLevel();
                    var barLength = Math.Min((int)(level * 50), 50);
                    var bar = new string('=', barLength).PadRight(50);

                    if (emitConsoleOutput)
                    {
                        if (sourceLabel != null)
                        {
                            Console.WriteLine($"[{sourceLabel}] Audio level: [{bar}]");
                        }
                        else
                        {
                            Console.WriteLine($"Audio level: [{bar}]");
                        }
                    }

                    await Task.Delay(500);
                }
                catch
                {
                    // Ignore visualization errors
                }
            }
        }

        private async Task ProcessAudioLoop()
        {
            try
            {
                foreach (var chunk in recorder.GetAudioChunks())
                {
                    if (cancellationTokenSource.Token.IsCancellationRequested)
                        break;

                    EnsureChunkSizeInitialized();
                    audioBuffer.AddRange(chunk);

                    int threshold = chunkSizeBytes;
                    int earlyThreshold = chunkSizeBytes; // default: no early processing

                    // Allow earlier processing only for microphone to reduce latency
                    if (recorder.IsMicrophone)
                    {
                        earlyThreshold = Math.Max(1, chunkSizeBytes / 3);
                    }

                    if (audioBuffer.Count >= threshold)
                    {
                        var audioToProcess = audioBuffer.ToArray();
                        audioBuffer.Clear();
                        var baseOffset = audioCursor;
                        AdvanceAudioCursor(audioToProcess.Length);
                        await ProcessAudioChunk(audioToProcess, baseOffset);
                    }
                    else if (recorder.IsMicrophone && audioBuffer.Count >= earlyThreshold)
                    {
                        var audioToProcess = audioBuffer.ToArray();
                        audioBuffer.Clear();
                        var baseOffset = audioCursor;
                        AdvanceAudioCursor(audioToProcess.Length);
                        await ProcessAudioChunk(audioToProcess, baseOffset);
                    }
                }

                if (audioBuffer.Count > 0)
                {
                    var baseOffset = audioCursor;
                    AdvanceAudioCursor(audioBuffer.Count);
                    await ProcessAudioChunk(audioBuffer.ToArray(), baseOffset);
                    audioBuffer.Clear();
                }
            }
            catch (Exception ex)
            {
                var message = $"Processing error: {ex.Message}";
                if (emitConsoleOutput)
                {
                    Console.Error.WriteLine(message);
                    Console.Error.WriteLine($"Stack trace: {ex.StackTrace}");
                }
                else
                {
                    onStatus?.Invoke(message);
                }
            }
        }

        private async Task ProcessAudioChunk(byte[] audioData, TimeSpan chunkBaseOffset)
        {
            try
            {
                using var wavStream = recorder.CreateWavStream(audioData);

                float[]? diarizationSamples = null;
                if (diarization != null)
                {
                    EnsureDiarizationInitialized();
                    if (diarizationSampleRate > 0)
                    {
                        var wavBytes = wavStream.ToArray();
                        diarizationSamples = AudioUtils.ReadWavBytesAsMono(wavBytes, diarizationSampleRate);
                        BufferDiarizationSamples(diarizationSamples, chunkBaseOffset);
                        diarization.Session.ProcessAudioChunk(diarizationSamples, chunkBaseOffset);
                        wavStream.Position = 0;
                    }
                }

                var items = new List<TranscriptionItem>();
                var pending = new List<PendingSegment>();

                await foreach (var result in processor.ProcessAsync(wavStream))
                {
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        var adjStart = chunkBaseOffset + result.Start;
                        var adjEnd = chunkBaseOffset + result.End;
                        var trimmed = result.Text.Trim();

                        if (diarization != null)
                        {
                            pending.Add(new PendingSegment(adjStart, adjEnd, trimmed, sourceLabel ?? string.Empty));
                        }
                        else
                        {
                            var taggedText = TranscriptionTagger.ApplyTags(trimmed, sourceLabel, null);
                            items.Add(new TranscriptionItem(adjStart, adjEnd, taggedText, sourceLabel ?? ""));

                            onText?.Invoke(taggedText);
                            if (emitConsoleOutput)
                            {
                                Console.WriteLine(taggedText);
                            }
                        }
                    }
                }

                if (diarization != null)
                {
                    if (pending.Count > 0)
                    {
                        pendingSegments.AddRange(pending);
                    }

                    ProcessDiarizationWindows();
                    FlushReadySegments(final: false);
                }

                EmitItems(items);
            }
            catch (Exception ex)
            {
                var message = $"Chunk processing error: {ex.Message}";
                if (emitConsoleOutput)
                {
                    Console.Error.WriteLine($"\n{message}");
                }
                else
                {
                    onStatus?.Invoke(message);
                }
            }
        }

        private void BufferDiarizationSamples(float[] samples, TimeSpan chunkBaseOffset)
        {
            if (samples.Length == 0 || diarizationSampleRate <= 0)
            {
                return;
            }

            if (diarizationBuffer.Count == 0)
            {
                diarizationBufferStart = chunkBaseOffset;
            }

            diarizationBuffer.AddRange(samples);
        }


        private void ProcessDiarizationWindows()
        {
            if (diarization == null)
            {
                return;
            }

            EnsureDiarizationInitialized();
            if (diarizationSampleRate <= 0 || diarizationWindowDuration <= TimeSpan.Zero)
            {
                return;
            }

            var bufferEnd = GetDiarizationBufferEnd();
            while (bufferEnd >= nextDiarizationWindowStart + diarizationWindowDuration)
            {
                var windowStart = nextDiarizationWindowStart;
                var windowEnd = windowStart + diarizationWindowDuration;
                ProcessWindow(windowStart, windowEnd);
                nextDiarizationWindowStart = windowEnd;
            }

            TrimDiarizationBuffer();
        }

        private void ProcessFinalDiarizationWindow()
        {
            if (diarization == null)
            {
                return;
            }

            EnsureDiarizationInitialized();
            var bufferEnd = GetDiarizationBufferEnd();
            if (bufferEnd <= nextDiarizationWindowStart)
            {
                return;
            }

            ProcessWindow(nextDiarizationWindowStart, bufferEnd);
            nextDiarizationWindowStart = bufferEnd;
            TrimDiarizationBuffer();
        }

        private void ProcessWindow(TimeSpan windowStart, TimeSpan windowEnd)
        {
            if (diarization == null)
            {
                return;
            }

            var windowSamples = ExtractWindowSamples(windowStart, windowEnd);
            if (windowSamples.Length == 0)
            {
                return;
            }

            var session = diarization.CreateSession();
            session.ProcessAudioChunk(windowSamples, windowStart);
            session.Complete();

            var snapshot = session.Timeline.Snapshot();
            var mappedTimeline = BuildGlobalTimeline(snapshot, windowSamples, windowStart);
            diarizationWindows.Add(new WindowDiarization(windowStart, windowEnd, mappedTimeline));
        }

        private float[] ExtractWindowSamples(TimeSpan windowStart, TimeSpan windowEnd)
        {
            if (diarizationSampleRate <= 0 || diarizationBuffer.Count == 0)
            {
                return Array.Empty<float>();
            }

            var startSample = (int)Math.Round((windowStart - diarizationBufferStart).TotalSeconds * diarizationSampleRate);
            var endSample = (int)Math.Round((windowEnd - diarizationBufferStart).TotalSeconds * diarizationSampleRate);
            startSample = Math.Clamp(startSample, 0, diarizationBuffer.Count);
            endSample = Math.Clamp(endSample, 0, diarizationBuffer.Count);
            if (endSample <= startSample)
            {
                return Array.Empty<float>();
            }

            var length = endSample - startSample;
            var windowSamples = new float[length];
            diarizationBuffer.CopyTo(startSample, windowSamples, 0, length);
            return windowSamples;
        }

        private DiarizationTimeline BuildGlobalTimeline(IReadOnlyList<DiarizationSegment> segments, float[] windowSamples, TimeSpan windowStart)
        {
            var ordered = new List<DiarizationSegment>(segments);
            ordered.Sort(static (a, b) => a.Start.CompareTo(b.Start));

            var map = BuildSpeakerMap(ordered, windowSamples, windowStart);
            var timeline = new DiarizationTimeline();
            foreach (var segment in ordered)
            {
                var mappedLabel = MapSpeakerLabel(segment.Speaker, map);
                timeline.Add(new DiarizationSegment(segment.Start, segment.End, mappedLabel));
            }

            return timeline;
        }

        private Dictionary<string, string> BuildSpeakerMap(IReadOnlyList<DiarizationSegment> segments, float[] windowSamples, TimeSpan windowStart)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (diarization == null || globalClusterer == null)
            {
                return map;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            int maxSamples = diarizationSampleRate > 0
                ? (int)Math.Round(diarizationSampleRate * 8.0)
                : windowSamples.Length;
            if (maxSamples <= 0)
            {
                maxSamples = windowSamples.Length;
            }

            foreach (var segment in segments)
            {
                var label = segment.Speaker;
                if (string.IsNullOrWhiteSpace(label) || IsOverlapLabel(label) || !seen.Add(label))
                {
                    continue;
                }

                var samples = CollectSpeakerSamples(segments, windowSamples, windowStart, label, maxSamples);
                if (samples.Length == 0)
                {
                    continue;
                }

                var embedding = diarization.ComputeEmbedding(samples);
                if (embedding.Length == 0)
                {
                    continue;
                }

                var assigned = globalClusterer.Assign(embedding);
                map[label] = assigned;
            }

            return map;
        }

        private float[] CollectSpeakerSamples(
            IReadOnlyList<DiarizationSegment> segments,
            float[] windowSamples,
            TimeSpan windowStart,
            string speaker,
            int maxSamples)
        {
            if (windowSamples.Length == 0 || maxSamples <= 0 || diarizationSampleRate <= 0)
            {
                return Array.Empty<float>();
            }

            var collected = new List<float>(Math.Min(maxSamples, windowSamples.Length));
            foreach (var segment in segments)
            {
                if (!string.Equals(segment.Speaker, speaker, StringComparison.Ordinal))
                {
                    continue;
                }

                var startSample = (int)Math.Round((segment.Start - windowStart).TotalSeconds * diarizationSampleRate);
                var endSample = (int)Math.Round((segment.End - windowStart).TotalSeconds * diarizationSampleRate);
                startSample = Math.Clamp(startSample, 0, windowSamples.Length);
                endSample = Math.Clamp(endSample, 0, windowSamples.Length);
                if (endSample <= startSample)
                {
                    continue;
                }

                var length = Math.Min(endSample - startSample, maxSamples - collected.Count);
                if (length <= 0)
                {
                    break;
                }

                for (int i = 0; i < length; i++)
                {
                    collected.Add(windowSamples[startSample + i]);
                }

                if (collected.Count >= maxSamples)
                {
                    break;
                }
            }

            return collected.ToArray();
        }

        private static bool IsOverlapLabel(string label)
        {
            return label.IndexOf('+', StringComparison.Ordinal) >= 0;
        }

        private static string MapSpeakerLabel(string label, Dictionary<string, string> map)
        {
            if (string.IsNullOrWhiteSpace(label) || map.Count == 0)
            {
                return label;
            }

            if (!IsOverlapLabel(label))
            {
                return map.TryGetValue(label, out var mapped) ? mapped : label;
            }

            var parts = label.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var mappedParts = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var part in parts)
            {
                if (map.TryGetValue(part, out var mapped))
                {
                    mappedParts.Add(mapped);
                }
                else
                {
                    mappedParts.Add(part);
                }
            }

            if (mappedParts.Count == 0)
            {
                return label;
            }

            return string.Join("+", mappedParts);
        }

        private string? ResolveSpeakerLabel(TimeSpan start, TimeSpan end)
        {
            if (diarizationWindows.Count == 0)
            {
                return lastEmittedSpeaker;
            }

            var target = start + TimeSpan.FromTicks((end - start).Ticks / 2);
            WindowDiarization? match = null;
            for (int i = 0; i < diarizationWindows.Count; i++)
            {
                var window = diarizationWindows[i];
                if (target >= window.Start && target < window.End)
                {
                    match = window;
                    break;
                }

                if (target >= window.End)
                {
                    match = window;
                }
            }

            if (match == null)
            {
                return lastEmittedSpeaker;
            }

            var label = match.Timeline.GetSpeakerLabel(start, end);
            return string.IsNullOrWhiteSpace(label) ? lastEmittedSpeaker : label;
        }

        private double GetEffectiveChunkSeconds()
        {
            if (diarization == null)
            {
                return chunkSizeSeconds;
            }

            var snapped = Math.Round(chunkSizeSeconds / DiarizationChunkSnapSeconds) * DiarizationChunkSnapSeconds;
            if (snapped <= 0)
            {
                snapped = DiarizationChunkSnapSeconds;
            }

            return snapped;
        }

        private static TimeSpan ResolveLiveDiarizationDelay()
        {
            var raw = Environment.GetEnvironmentVariable("TX_LIVE_DIARIZATION_DELAY_SECONDS");
            if (!string.IsNullOrWhiteSpace(raw)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(TranscribeDefaults.DefaultLiveDiarizationDelaySeconds);
        }

        private void FlushReadySegments(bool final)
        {
            if (pendingSegments.Count == 0)
            {
                return;
            }

            var eligibleTime = final ? GetDiarizationBufferEnd() : audioCursor - diarizationDelay;
            if (!final && eligibleTime <= TimeSpan.Zero)
            {
                return;
            }

            var ready = new List<PendingSegment>();
            var remaining = new List<PendingSegment>(pendingSegments.Count);
            foreach (var segment in pendingSegments)
            {
                if (segment.End <= eligibleTime)
                {
                    ready.Add(segment);
                }
                else
                {
                    remaining.Add(segment);
                }
            }

            if (ready.Count == 0)
            {
                return;
            }

            pendingSegments.Clear();
            pendingSegments.AddRange(remaining);

            ready.Sort(static (a, b) => a.Start.CompareTo(b.Start));
            var items = new List<TranscriptionItem>(ready.Count);
            foreach (var segment in ready)
            {
                var speakerLabel = ResolveSpeakerLabel(segment.Start, segment.End);
                if (!string.IsNullOrWhiteSpace(speakerLabel))
                {
                    lastEmittedSpeaker = speakerLabel;
                }

                var taggedText = TranscriptionTagger.ApplyTags(segment.Text, sourceLabel, speakerLabel);
                onText?.Invoke(taggedText);
                if (emitConsoleOutput)
                {
                    Console.WriteLine(taggedText);
                }

                items.Add(new TranscriptionItem(segment.Start, segment.End, taggedText, segment.Source));
            }

            EmitItems(items);
            TrimDiarizationWindows(eligibleTime);
        }

        private void EmitItems(List<TranscriptionItem> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            if (merger != null)
            {
                merger.Submit(items);
                return;
            }

            foreach (var item in items)
            {
                transcriptWriter.WriteLine($"{item.Start}->{item.End}: {item.Text}");
            }
        }

        private void TrimDiarizationWindows(TimeSpan cutoff)
        {
            if (diarizationWindows.Count == 0)
            {
                return;
            }

            int removeCount = 0;
            while (removeCount < diarizationWindows.Count && diarizationWindows[removeCount].End <= cutoff)
            {
                removeCount++;
            }

            if (removeCount > 0)
            {
                diarizationWindows.RemoveRange(0, removeCount);
            }
        }

        private void TrimDiarizationBuffer()
        {
            if (diarizationBuffer.Count == 0 || diarizationSampleRate <= 0)
            {
                return;
            }

            if (nextDiarizationWindowStart <= diarizationBufferStart)
            {
                return;
            }

            var dropSamples = (int)Math.Round((nextDiarizationWindowStart - diarizationBufferStart).TotalSeconds * diarizationSampleRate);
            if (dropSamples <= 0)
            {
                return;
            }

            if (dropSamples >= diarizationBuffer.Count)
            {
                diarizationBuffer.Clear();
                diarizationBufferStart = nextDiarizationWindowStart;
                return;
            }

            diarizationBuffer.RemoveRange(0, dropSamples);
            diarizationBufferStart = nextDiarizationWindowStart;
        }

        private TimeSpan GetDiarizationBufferEnd()
        {
            if (diarizationSampleRate <= 0)
            {
                return diarizationBufferStart;
            }

            return diarizationBufferStart + TimeSpan.FromSeconds(diarizationBuffer.Count / (double)diarizationSampleRate);
        }

        private void FinalizeDiarization()
        {
            if (diarization == null || diarizationFinalized)
            {
                return;
            }

            diarizationFinalized = true;
            EnsureDiarizationInitialized();
            ProcessDiarizationWindows();
            ProcessFinalDiarizationWindow();
            FlushReadySegments(final: true);

            diarization.Session.Complete();
        }

        private void AdvanceAudioCursor(int byteCount)
        {
            int rate = bytesPerSecond > 0 ? bytesPerSecond : 32000;
            audioCursor += TimeSpan.FromSeconds(byteCount / (double)rate);
        }

        public async Task StopProcessing()
        {
            cancellationTokenSource.Cancel();
            if (processingTask != null)
            {
                await processingTask;
            }
            if (visualizationTask != null)
            {
                await visualizationTask;
            }

            FinalizeDiarization();
        }

        public async Task WaitForCompletionAsync()
        {
            if (processingTask != null)
            {
                await processingTask;
            }
            if (visualizationTask != null)
            {
                await visualizationTask;
            }

            FinalizeDiarization();
        }

        public void Dispose()
        {
            cancellationTokenSource.Cancel();
            processingTask?.Wait();
            visualizationTask?.Wait();

            FinalizeDiarization();

            if (merger == null)
            {
                transcriptWriter?.Flush();
                transcriptWriter?.Dispose();
            }

            diarization?.Dispose();

            processor?.Dispose();
            whisperFactory?.Dispose();
            cancellationTokenSource.Dispose();
        }
    }
}
