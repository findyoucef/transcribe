using System;
using System.Collections.Generic;

namespace Transcribe
{
    public sealed class DiarizationSession
    {
        private readonly SpeakerEmbeddingModel model;
        private readonly OnlineClusterer clusterer;
        private readonly DiarizationTimeline timeline = new();
        private readonly Action<string>? log;
        private readonly int sampleRate;
        private readonly int frameSize;
        private readonly int hopSize;
        private readonly int minSpeechSamples;
        private readonly int minSilenceFrames;
        private readonly int maxSegmentSamples;
        private readonly float energyThreshold;
        private readonly float similarityThreshold;
        private readonly float fallbackSimilarityThreshold;
        private readonly float noiseMultiplier = 3f;
        private readonly int windowSamples;
        private readonly int hopWindowSamples;
        private readonly int maxBufferedSamples;
        private readonly int minSpeakerWindows;
        private readonly float activeMatchMargin;
        private readonly float overlapMinSimilarity;
        private readonly float overlapMaxGap;
        private readonly int minNewSpeakerWindows;
        private readonly float instantSwitchSimilarity;
        private readonly int minReturnSpeakerWindows;
        private readonly TimeSpan minReturnHoldDuration;
        private readonly float strongMatchThreshold;
        private readonly bool logScores;

        private bool inSpeech;
        private int silenceFrames;
        private TimeSpan? segmentStart;
        private readonly List<float> segmentSamples = new();
        private readonly List<float> residualSamples = new();
        private float noiseFloor = 0.005f;
        private TimeSpan lastProcessedTime = TimeSpan.Zero;
        private int windowOffsetSamples;
        private string? activeSpeaker;
        private TimeSpan activeSpeakerStart;
        private TimeSpan activeSpeakerEnd;
        private string? activeOverlapLabel;
        private TimeSpan activeOverlapStart;
        private TimeSpan activeOverlapEnd;
        private string? lastSpeaker;
        private string? candidateSpeaker;
        private bool candidateIsNew;
        private int candidateWindows;
        private TimeSpan candidateStart;
        private float[]? candidateEmbeddingSum;
        private int candidateEmbeddingCount;

        public DiarizationSession(SpeakerEmbeddingModel model, DiarizationOptions options, Action<string>? log = null)
        {
            this.model = model;
            this.log = log;
            clusterer = new OnlineClusterer(options.SimilarityThreshold);
            sampleRate = model.Config.SampleRate;
            similarityThreshold = options.SimilarityThreshold;
            fallbackSimilarityThreshold = Math.Clamp(options.FallbackSimilarityThreshold, 0.4f, similarityThreshold);

            // VAD frame sizes in ms.
            const int frameMs = 20;
            frameSize = (int)Math.Round(sampleRate * (frameMs / 1000.0));
            hopSize = frameSize;

            minSpeechSamples = (int)Math.Round(sampleRate * (options.MinSpeechMs / 1000.0));
            minSilenceFrames = Math.Max(1, options.MinSilenceMs / frameMs);
            maxSegmentSamples = Math.Max(frameSize, options.MaxSegmentSeconds * sampleRate);
            energyThreshold = options.EnergyThreshold;

            var windowSeconds = Math.Max(0.5f, options.WindowSeconds);
            var hopSeconds = Math.Clamp(options.WindowHopSeconds, 0.1f, windowSeconds);
            windowSamples = Math.Max(frameSize, (int)Math.Round(sampleRate * windowSeconds));
            hopWindowSamples = Math.Max(frameSize, (int)Math.Round(sampleRate * hopSeconds));
            maxBufferedSamples = Math.Max(windowSamples * 3, maxSegmentSamples);
            minSpeakerWindows = Math.Max(1, options.MinSpeakerWindows);
            activeMatchMargin = Math.Clamp(options.ActiveMatchMargin, 0f, 0.2f);
            overlapMinSimilarity = Math.Clamp(options.OverlapMinSimilarity, 0f, similarityThreshold);
            overlapMaxGap = Math.Clamp(options.OverlapMaxGap, 0f, 0.3f);
            minNewSpeakerWindows = Math.Max(minSpeakerWindows, options.MinNewSpeakerWindows);
            instantSwitchSimilarity = Math.Clamp(options.InstantSwitchSimilarity, similarityThreshold, 0.95f);
            minReturnSpeakerWindows = Math.Max(1, minSpeakerWindows / 2);
            minReturnHoldDuration = TimeSpan.FromSeconds((windowSamples * 2) / (double)sampleRate);
            strongMatchThreshold = Math.Min(0.9f, similarityThreshold + 0.05f);
            logScores = options.LogScores;
        }

        public DiarizationTimeline Timeline => timeline;
        public int SampleRate => sampleRate;

        public void ProcessAudioChunk(float[] samples, TimeSpan chunkBaseOffset)
        {
            if (samples.Length == 0)
            {
                return;
            }

            var combined = CombineResidual(samples, chunkBaseOffset, out var adjustedBaseOffset);
            int offset = 0;
            var frameDuration = TimeSpan.FromSeconds(frameSize / (double)sampleRate);

            while (offset + frameSize <= combined.Length)
            {
                var frameStart = adjustedBaseOffset + TimeSpan.FromSeconds(offset / (double)sampleRate);
                var rms = ComputeRms(combined, offset, frameSize);

                bool speech = IsSpeech(rms);
                if (speech)
                {
                    if (!inSpeech)
                    {
                        inSpeech = true;
                        segmentStart = frameStart;
                        segmentSamples.Clear();
                        silenceFrames = 0;
                        windowOffsetSamples = 0;
                        ResetCandidate();
                    }

                    AppendSamples(combined, offset, frameSize);
                    silenceFrames = 0;

                    if (segmentStart.HasValue)
                    {
                        ProcessWindows(segmentStart.Value);
                        TrimSegmentBuffer();
                    }
                }
                else
                {
                    if (inSpeech)
                    {
                        silenceFrames++;
                        if (silenceFrames >= minSilenceFrames)
                        {
                            var segmentEnd = frameStart;
                            FinalizeSegment(segmentEnd);
                            inSpeech = false;
                            silenceFrames = 0;
                        }
                    }
                    else
                    {
                        noiseFloor = (noiseFloor * 0.95f) + (rms * 0.05f);
                    }
                }

                lastProcessedTime = frameStart + frameDuration;
                offset += hopSize;
            }

            StoreResidual(combined, offset);
        }

        public void Complete()
        {
            if (inSpeech && segmentStart.HasValue)
            {
                FinalizeSegment(lastProcessedTime);
                inSpeech = false;
            }

            CloseActiveSpeakerSegment(lastProcessedTime);
            CloseOverlapSegment(lastProcessedTime);
        }

        public string? GetSpeakerLabel(TimeSpan start, TimeSpan end)
        {
            return timeline.GetSpeakerLabel(start, end);
        }

        private bool IsSpeech(float rms)
        {
            var threshold = Math.Max(energyThreshold, noiseFloor * noiseMultiplier);
            return rms >= threshold;
        }

        private void FinalizeSegment(TimeSpan segmentEnd)
        {
            if (!segmentStart.HasValue)
            {
                return;
            }

            if (segmentSamples.Count < minSpeechSamples)
            {
                segmentSamples.Clear();
                segmentStart = null;
                return;
            }

            ProcessWindows(segmentStart.Value);
            FlushTrailingWindow(segmentStart.Value, segmentEnd);
            CloseOverlapSegment(segmentEnd);
            CloseActiveSpeakerSegment(segmentEnd);

            segmentSamples.Clear();
            segmentStart = null;
            windowOffsetSamples = 0;
            ResetCandidate();
        }

        private void ProcessWindows(TimeSpan segmentBase)
        {
            while (segmentSamples.Count >= windowOffsetSamples + windowSamples)
            {
                var windowStart = segmentBase + TimeSpan.FromSeconds(windowOffsetSamples / (double)sampleRate);
                var windowEnd = windowStart + TimeSpan.FromSeconds(hopWindowSamples / (double)sampleRate);

                var window = new float[windowSamples];
                segmentSamples.CopyTo(windowOffsetSamples, window, 0, windowSamples);
                var embedding = model.ComputeEmbedding(window);
                if (embedding.Length > 0)
                {
                    HandleWindow(embedding, windowStart, windowEnd);
                }

                windowOffsetSamples += hopWindowSamples;
            }
        }

        private void FlushTrailingWindow(TimeSpan segmentBase, TimeSpan segmentEnd)
        {
            if (segmentSamples.Count < windowSamples)
            {
                return;
            }

            var startOffset = Math.Max(0, segmentSamples.Count - windowSamples);
            if (startOffset < windowOffsetSamples)
            {
                return;
            }

            var windowStart = segmentBase + TimeSpan.FromSeconds(startOffset / (double)sampleRate);
            var window = new float[windowSamples];
            segmentSamples.CopyTo(startOffset, window, 0, windowSamples);
            var embedding = model.ComputeEmbedding(window);
            if (embedding.Length == 0)
            {
                return;
            }

            HandleWindow(embedding, windowStart, segmentEnd);
        }

        private void HandleWindow(float[] embedding, TimeSpan windowStart, TimeSpan windowEnd)
        {
            if (embedding.Length == 0)
            {
                return;
            }

            var topTwo = clusterer.FindTopTwo(embedding);
            float activeScore = -1f;
            int activeIndex = -1;
            bool hasActiveScore = activeSpeaker != null
                && clusterer.TryGetSimilarity(activeSpeaker, embedding, out activeScore, out activeIndex);

            if (logScores && log != null)
            {
                var activeLabel = activeSpeaker ?? "none";
                var bestLabel = topTwo.First.Label;
                var secondLabel = topTwo.Second.Label;
                log($"Diarization scores [{windowStart}->{windowEnd}] active={activeLabel}:{activeScore:F3} best={bestLabel}:{topTwo.First.Score:F3} second={secondLabel}:{topTwo.Second.Score:F3}");
            }
            string? overlapLabel = null;

            if (topTwo.Second.Index >= 0
                && topTwo.First.Score >= overlapMinSimilarity
                && topTwo.Second.Score >= overlapMinSimilarity
                && (topTwo.First.Score - topTwo.Second.Score) <= overlapMaxGap)
            {
                overlapLabel = BuildOverlapLabel(topTwo.First.Label, topTwo.Second.Label);
            }

            if (hasActiveScore)
            {
                bool keepActive = activeScore >= strongMatchThreshold;
                if (!keepActive && activeScore >= similarityThreshold - activeMatchMargin)
                {
                    if (topTwo.First.Index >= 0
                        && string.Equals(topTwo.First.Label, activeSpeaker, StringComparison.Ordinal))
                    {
                        keepActive = topTwo.Second.Index < 0
                            || (topTwo.First.Score - topTwo.Second.Score) > activeMatchMargin;
                    }
                }
                if (!keepActive
                    && activeScore >= similarityThreshold
                    && topTwo.First.Index >= 0
                    && !string.Equals(topTwo.First.Label, activeSpeaker, StringComparison.Ordinal)
                    && (topTwo.First.Score - activeScore) <= overlapMaxGap)
                {
                    keepActive = true;
                }

                if (keepActive)
                {
                    ResetCandidate();
                    if (!string.IsNullOrWhiteSpace(overlapLabel))
                    {
                        ExtendOrStartSpeakerSegment(activeSpeaker!, windowStart, windowEnd);
                        ExtendOrStartOverlapSegment(overlapLabel, windowStart, windowEnd);
                        return;
                    }

                    clusterer.UpdateCluster(activeIndex, embedding);
                    ExtendOrStartSpeakerSegment(activeSpeaker!, windowStart, windowEnd);
                    CloseOverlapSegment(windowStart);
                    return;
                }
            }

            if (!string.IsNullOrWhiteSpace(overlapLabel))
            {
                ResetCandidate();
                if (topTwo.First.Index >= 0 && !string.IsNullOrWhiteSpace(topTwo.First.Label))
                {
                    ExtendOrStartSpeakerSegment(topTwo.First.Label, windowStart, windowEnd);
                }
                ExtendOrStartOverlapSegment(overlapLabel, windowStart, windowEnd);
                return;
            }

            CloseOverlapSegment(windowStart);

            var best = topTwo.First;
            var hasBest = best.Index >= 0;
            var bestGap = topTwo.Second.Index >= 0 ? best.Score - topTwo.Second.Score : 1f;

            if (hasBest
                && activeSpeaker != null
                && string.Equals(best.Label, activeSpeaker, StringComparison.Ordinal)
                && best.Score < strongMatchThreshold
                && bestGap <= activeMatchMargin)
            {
                hasBest = false;
            }

            var strongMatch = hasBest && best.Score >= strongMatchThreshold;
            var canReturn = !hasActiveScore || activeScore < similarityThreshold;
            var returnMatch = hasBest
                && canReturn
                && !strongMatch
                && best.Score >= fallbackSimilarityThreshold
                && bestGap >= activeMatchMargin
                && !string.IsNullOrWhiteSpace(lastSpeaker)
                && string.Equals(best.Label, lastSpeaker, StringComparison.Ordinal);
            var weakExistingMatch = hasBest
                && canReturn
                && !strongMatch
                && !returnMatch
                && best.Score >= fallbackSimilarityThreshold
                && bestGap >= activeMatchMargin
                && (activeSpeaker == null || !string.Equals(best.Label, activeSpeaker, StringComparison.Ordinal));

            if (strongMatch || returnMatch)
            {
                if (activeSpeaker == null || string.Equals(best.Label, activeSpeaker, StringComparison.Ordinal))
                {
                    if (strongMatch)
                    {
                        clusterer.UpdateCluster(best.Index, embedding);
                    }
                    ResetCandidate();
                    ExtendOrStartSpeakerSegment(best.Label, windowStart, windowEnd);
                    return;
                }

                if (strongMatch
                    && hasActiveScore
                    && (best.Score - activeScore) >= activeMatchMargin)
                {
                    ResetCandidate();
                    SwitchToExistingSpeaker(best.Index, best.Label, embedding, windowStart, windowEnd);
                }
                else if (strongMatch && best.Score >= instantSwitchSimilarity)
                {
                    ResetCandidate();
                    SwitchToExistingSpeaker(best.Index, best.Label, embedding, windowStart, windowEnd);
                }
                else
                {
                    AdvanceCandidateExisting(best.Label, windowStart);
                    var requiredWindows = GetRequiredExistingWindows(best.Label);
                    var allowFastReturn = returnMatch
                        && activeSpeaker != null
                        && (windowStart - activeSpeakerStart) >= minReturnHoldDuration;
                    if (candidateWindows >= requiredWindows
                        || (allowFastReturn && candidateWindows >= 1))
                    {
                        SwitchToExistingSpeaker(best.Index, best.Label, embedding, windowStart, windowEnd);
                        ResetCandidate();
                    }
                    else if (activeSpeaker != null)
                    {
                        ExtendOrStartSpeakerSegment(activeSpeaker!, windowStart, windowEnd);
                    }
                }
                return;
            }

            if (weakExistingMatch)
            {
                AdvanceCandidateExisting(best.Label, windowStart);
                var allowFastReturn = activeSpeaker != null
                    && (windowStart - activeSpeakerStart) >= minReturnHoldDuration;
                if (candidateWindows >= minReturnSpeakerWindows
                    || (allowFastReturn && candidateWindows >= 1))
                {
                    SwitchToExistingSpeaker(best.Index, best.Label, embedding, candidateStart, windowEnd);
                    ResetCandidate();
                }
                else if (activeSpeaker != null)
                {
                    ExtendOrStartSpeakerSegment(activeSpeaker!, windowStart, windowEnd);
                }
                return;
            }

            AdvanceCandidateNew(embedding, windowStart);
            if (candidateWindows >= minNewSpeakerWindows)
            {
                var candidateEmbedding = GetCandidateMeanEmbedding(embedding);
                var bestCandidate = clusterer.FindBest(candidateEmbedding);
                if (bestCandidate.Index >= 0 && bestCandidate.Score >= fallbackSimilarityThreshold)
                {
                    SwitchToExistingSpeaker(bestCandidate.Index, bestCandidate.Label, candidateEmbedding, windowStart, windowEnd);
                }
                else
                {
                    var newLabel = clusterer.CreateCluster(candidateEmbedding);
                    SwitchToNewSpeaker(newLabel, windowStart, windowEnd);
                }
                ResetCandidate();
            }
            else if (activeSpeaker != null)
            {
                ExtendOrStartSpeakerSegment(activeSpeaker!, windowStart, windowEnd);
            }
        }

        private void AdvanceCandidateExisting(string label, TimeSpan windowStart)
        {
            if (!candidateIsNew && string.Equals(candidateSpeaker, label, StringComparison.Ordinal))
            {
                candidateWindows++;
                return;
            }

            candidateSpeaker = label;
            candidateIsNew = false;
            candidateWindows = 1;
            candidateStart = windowStart;
            candidateEmbeddingSum = null;
            candidateEmbeddingCount = 0;
        }

        private void AdvanceCandidateNew(float[] embedding, TimeSpan windowStart)
        {
            if (candidateIsNew)
            {
                candidateWindows++;
                AddCandidateEmbedding(embedding);
                return;
            }

            candidateSpeaker = null;
            candidateIsNew = true;
            candidateWindows = 1;
            candidateStart = windowStart;
            candidateEmbeddingSum = null;
            candidateEmbeddingCount = 0;
            AddCandidateEmbedding(embedding);
        }

        private void AddCandidateEmbedding(float[] embedding)
        {
            if (embedding.Length == 0)
            {
                return;
            }

            if (candidateEmbeddingSum == null)
            {
                candidateEmbeddingSum = (float[])embedding.Clone();
                candidateEmbeddingCount = 1;
                return;
            }

            candidateEmbeddingCount++;
            var length = Math.Min(candidateEmbeddingSum.Length, embedding.Length);
            for (int i = 0; i < length; i++)
            {
                candidateEmbeddingSum[i] += embedding[i];
            }
        }

        private float[] GetCandidateMeanEmbedding(float[] fallback)
        {
            if (candidateEmbeddingSum == null || candidateEmbeddingCount <= 1)
            {
                return fallback;
            }

            var mean = new float[candidateEmbeddingSum.Length];
            var inv = 1f / candidateEmbeddingCount;
            for (int i = 0; i < candidateEmbeddingSum.Length; i++)
            {
                mean[i] = candidateEmbeddingSum[i] * inv;
            }

            return mean;
        }

        private void ResetCandidate()
        {
            candidateSpeaker = null;
            candidateIsNew = false;
            candidateWindows = 0;
            candidateStart = TimeSpan.Zero;
            candidateEmbeddingSum = null;
            candidateEmbeddingCount = 0;
        }

        private int GetRequiredExistingWindows(string label)
        {
            if (!string.IsNullOrWhiteSpace(lastSpeaker)
                && !string.IsNullOrWhiteSpace(label)
                && string.Equals(label, lastSpeaker, StringComparison.Ordinal))
            {
                return minReturnSpeakerWindows;
            }

            return minSpeakerWindows;
        }

        private static string BuildOverlapLabel(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
            {
                return second;
            }

            if (string.IsNullOrWhiteSpace(second))
            {
                return first;
            }

            return string.CompareOrdinal(first, second) <= 0
                ? $"{first}+{second}"
                : $"{second}+{first}";
        }

        private void ExtendOrStartSpeakerSegment(string speaker, TimeSpan start, TimeSpan end)
        {
            if (string.IsNullOrWhiteSpace(speaker))
            {
                return;
            }

            if (activeSpeaker != null && string.Equals(activeSpeaker, speaker, StringComparison.Ordinal))
            {
                if (end > activeSpeakerEnd)
                {
                    activeSpeakerEnd = end;
                }
                return;
            }

            if (activeSpeaker != null)
            {
                timeline.Add(new DiarizationSegment(activeSpeakerStart, activeSpeakerEnd, activeSpeaker));
                log?.Invoke($"Diarization segment: {activeSpeakerStart}->{activeSpeakerEnd} {activeSpeaker}");
            }

            activeSpeaker = speaker;
            activeSpeakerStart = start;
            activeSpeakerEnd = end;
        }

        private void ExtendOrStartOverlapSegment(string label, TimeSpan start, TimeSpan end)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            var maxGap = TimeSpan.FromSeconds(hopWindowSamples / (double)sampleRate);
            if (activeOverlapLabel != null && string.Equals(activeOverlapLabel, label, StringComparison.Ordinal))
            {
                if (start <= activeOverlapEnd + maxGap)
                {
                    if (end > activeOverlapEnd)
                    {
                        activeOverlapEnd = end;
                    }
                    return;
                }
            }

            if (activeOverlapLabel != null)
            {
                timeline.Add(new DiarizationSegment(activeOverlapStart, activeOverlapEnd, activeOverlapLabel));
                log?.Invoke($"Overlap segment: {activeOverlapStart}->{activeOverlapEnd} {activeOverlapLabel}");
            }

            activeOverlapLabel = label;
            activeOverlapStart = start;
            activeOverlapEnd = end;
        }

        private void CloseOverlapSegment(TimeSpan end)
        {
            if (activeOverlapLabel == null)
            {
                return;
            }

            var closeEnd = end > activeOverlapEnd ? end : activeOverlapEnd;
            timeline.Add(new DiarizationSegment(activeOverlapStart, closeEnd, activeOverlapLabel));
            log?.Invoke($"Overlap segment: {activeOverlapStart}->{closeEnd} {activeOverlapLabel}");

            activeOverlapLabel = null;
            activeOverlapStart = TimeSpan.Zero;
            activeOverlapEnd = TimeSpan.Zero;
        }

        private void CloseActiveSpeakerSegment(TimeSpan end)
        {
            if (activeSpeaker == null)
            {
                return;
            }

            var closeEnd = end > activeSpeakerEnd ? end : activeSpeakerEnd;
            timeline.Add(new DiarizationSegment(activeSpeakerStart, closeEnd, activeSpeaker));
            log?.Invoke($"Diarization segment: {activeSpeakerStart}->{closeEnd} {activeSpeaker}");

            activeSpeaker = null;
            activeSpeakerStart = TimeSpan.Zero;
            activeSpeakerEnd = TimeSpan.Zero;
        }

        private void TruncateActiveSpeakerEnd(TimeSpan end)
        {
            if (activeSpeaker == null)
            {
                return;
            }

            if (activeSpeakerEnd > end)
            {
                activeSpeakerEnd = end;
            }
        }

        private void SwitchToExistingSpeaker(int index, string label, float[] embedding, TimeSpan switchStart, TimeSpan windowEnd)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            var previous = activeSpeaker;
            TruncateActiveSpeakerEnd(switchStart);
            CloseActiveSpeakerSegment(switchStart);
            clusterer.UpdateCluster(index, embedding);
            UpdateLastSpeaker(previous, label);
            ExtendOrStartSpeakerSegment(label, switchStart, windowEnd);
        }

        private void SwitchToNewSpeaker(string label, TimeSpan switchStart, TimeSpan windowEnd)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            var previous = activeSpeaker;
            TruncateActiveSpeakerEnd(switchStart);
            CloseActiveSpeakerSegment(switchStart);
            UpdateLastSpeaker(previous, label);
            ExtendOrStartSpeakerSegment(label, switchStart, windowEnd);
        }

        private void UpdateLastSpeaker(string? previous, string current)
        {
            if (!string.IsNullOrWhiteSpace(previous)
                && !string.Equals(previous, current, StringComparison.Ordinal))
            {
                lastSpeaker = previous;
            }
        }

        private void TrimSegmentBuffer()
        {
            if (segmentSamples.Count <= maxBufferedSamples)
            {
                return;
            }

            var drop = segmentSamples.Count - maxBufferedSamples;
            segmentSamples.RemoveRange(0, drop);
            if (segmentStart.HasValue)
            {
                segmentStart = segmentStart.Value + TimeSpan.FromSeconds(drop / (double)sampleRate);
            }
            windowOffsetSamples = Math.Max(0, windowOffsetSamples - drop);
        }

        private void AppendSamples(float[] samples, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                segmentSamples.Add(samples[offset + i]);
            }
        }

        private static float ComputeRms(float[] samples, int offset, int count)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
            {
                float v = samples[offset + i];
                sum += v * v;
            }

            return (float)Math.Sqrt(sum / count);
        }

        private float[] CombineResidual(float[] samples, TimeSpan chunkBaseOffset, out TimeSpan adjustedBaseOffset)
        {
            if (residualSamples.Count == 0)
            {
                adjustedBaseOffset = chunkBaseOffset;
                return samples;
            }

            var combined = new float[residualSamples.Count + samples.Length];
            residualSamples.CopyTo(combined, 0);
            Array.Copy(samples, 0, combined, residualSamples.Count, samples.Length);

            adjustedBaseOffset = chunkBaseOffset - TimeSpan.FromSeconds(residualSamples.Count / (double)sampleRate);
            residualSamples.Clear();
            return combined;
        }

        private void StoreResidual(float[] combined, int offset)
        {
            if (offset >= combined.Length)
            {
                residualSamples.Clear();
                return;
            }

            residualSamples.Clear();
            for (int i = offset; i < combined.Length; i++)
            {
                residualSamples.Add(combined[i]);
            }
        }
    }
}
