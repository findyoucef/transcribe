using Microsoft.Extensions.AI;
using OllamaSharp;

namespace transcribe
{
    public class OllamaClient
    {
        IChatClient _client;

        public OllamaClient(string hostname, string model) 
        {
            // Initialize OllamaApiClient targeting the "qwen3-coder-p4:latest" model
            _client = new OllamaApiClient(new Uri(hostname), model);

            _client = ChatClientBuilderChatClientExtensions
                .AsBuilder(_client)
                    .UseFunctionInvocation()
                    .Build();
        }

        public async Task SummarizeTranscript(string transcript, Action<string>? onUpdate = null)
        {
            // Conversational loop that can utilize the tools via prompts.
            var messages = new List<ChatMessage>();
            messages.Add(new(ChatRole.User, transcript));

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in _client
                .GetStreamingResponseAsync(messages))
            {
                var text = update?.ToString() ?? string.Empty;
                if (onUpdate != null)
                {
                    onUpdate(text);
                }
                else
                {
                    Console.Write(text);
                }

                if (update != null)
                {
                    updates.Add(update);
                }
            }

            if (onUpdate == null)
            {
                Console.WriteLine();
            }

            messages.AddMessages(updates);
        }

        public async Task SummarizeTranscript(string transcript, string outputFilePath, Action<string>? onUpdate = null)
        {
            if (string.IsNullOrWhiteSpace(transcript))
            {
                throw new ArgumentException("Transcript cannot be null or whitespace.", nameof(transcript));
            }

            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                throw new ArgumentException("Output file path cannot be null or whitespace.", nameof(outputFilePath));
            }

            var directory = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var instructions = "DETAIL: high\r\nFOCUS: include every major thread you detect, not just the last topic\r\nTEXT: ";

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, instructions + transcript)
            };

            List<ChatResponseUpdate> updates = [];

            await using var stream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);

            await foreach (ChatResponseUpdate update in _client.GetStreamingResponseAsync(messages))
            {
                var text = update?.ToString() ?? string.Empty;
                await writer.WriteAsync(text);
                onUpdate?.Invoke(text);
                if (update != null)
                {
                    updates.Add(update);
                }
            }

            await writer.FlushAsync();

            messages.AddMessages(updates);
        }
    }
}
