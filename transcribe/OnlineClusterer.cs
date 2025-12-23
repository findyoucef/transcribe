using System;

namespace Transcribe
{
    public sealed class OnlineClusterer
    {
        private readonly List<SpeakerCluster> clusters = new();
        private readonly float similarityThreshold;

        public OnlineClusterer(float similarityThreshold)
        {
            this.similarityThreshold = similarityThreshold;
        }

        public ClusterMatch FindBest(float[] embedding)
        {
            if (embedding.Length == 0)
            {
                return new ClusterMatch(-1, string.Empty, -1f);
            }

            int bestIndex = -1;
            float bestScore = -1f;

            for (int i = 0; i < clusters.Count; i++)
            {
                float score = CosineSimilarity(embedding, clusters[i].Centroid);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0)
            {
                return new ClusterMatch(-1, string.Empty, -1f);
            }

            return new ClusterMatch(bestIndex, clusters[bestIndex].Label, bestScore);
        }

        public (ClusterMatch First, ClusterMatch Second) FindTopTwo(float[] embedding)
        {
            var first = new ClusterMatch(-1, string.Empty, -1f);
            var second = new ClusterMatch(-1, string.Empty, -1f);

            if (embedding.Length == 0)
            {
                return (first, second);
            }

            for (int i = 0; i < clusters.Count; i++)
            {
                float score = CosineSimilarity(embedding, clusters[i].Centroid);
                if (score > first.Score)
                {
                    second = first;
                    first = new ClusterMatch(i, clusters[i].Label, score);
                }
                else if (score > second.Score)
                {
                    second = new ClusterMatch(i, clusters[i].Label, score);
                }
            }

            return (first, second);
        }

        public bool TryGetSimilarity(string label, float[] embedding, out float score, out int index)
        {
            score = -1f;
            index = -1;
            if (embedding.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < clusters.Count; i++)
            {
                if (!string.Equals(clusters[i].Label, label, StringComparison.Ordinal))
                {
                    continue;
                }

                score = CosineSimilarity(embedding, clusters[i].Centroid);
                index = i;
                return true;
            }

            return false;
        }

        public void UpdateCluster(int index, float[] embedding)
        {
            if (index < 0 || index >= clusters.Count)
            {
                return;
            }

            clusters[index].Update(embedding);
        }

        public string CreateCluster(float[] embedding)
        {
            var label = $"S{clusters.Count + 1}";
            clusters.Add(new SpeakerCluster(label, embedding));
            return label;
        }

        public string Assign(float[] embedding)
        {
            var match = FindBest(embedding);
            if (match.Index >= 0 && match.Score >= similarityThreshold)
            {
                clusters[match.Index].Update(embedding);
                return match.Label;
            }

            return CreateCluster(embedding);
        }

        private static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0f;
            float normA = 0f;
            float normB = 0f;
            var length = Math.Min(a.Length, b.Length);
            for (int i = 0; i < length; i++)
            {
                dot += a[i] * b[i];
                normA += a[i] * a[i];
                normB += b[i] * b[i];
            }

            if (normA <= 0 || normB <= 0)
            {
                return 0f;
            }

            return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
        }

        private sealed class SpeakerCluster
        {
            public SpeakerCluster(string label, float[] centroid)
            {
                Label = label;
                Centroid = (float[])centroid.Clone();
                Count = 1;
            }

            public string Label { get; }
            public float[] Centroid { get; private set; }
            public int Count { get; private set; }

            public void Update(float[] embedding)
            {
                Count++;
                for (int i = 0; i < Centroid.Length && i < embedding.Length; i++)
                {
                    Centroid[i] = Centroid[i] + ((embedding[i] - Centroid[i]) / Count);
                }
            }
        }

        public readonly record struct ClusterMatch(int Index, string Label, float Score);
    }
}
