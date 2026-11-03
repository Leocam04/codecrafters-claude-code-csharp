using System.ClientModel;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

if (args.Length < 2 || args[0] != "-p")
{
    throw new Exception("Usage: program -p <prompt>");
}

var prompt = args[1];

if (string.IsNullOrEmpty(prompt))
{
    throw new Exception("Prompt must not be empty");
}

var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
var baseUrl = Environment.GetEnvironmentVariable("OPENROUTER_BASE_URL") ?? "https://openrouter.ai/api/v1";

if (string.IsNullOrEmpty(apiKey))
{
    throw new Exception("OPENROUTER_API_KEY is not set");
}

ChatTool readTool = ChatTool.CreateFunctionTool(
    functionName: "Read",
    functionDescription: "Read and return the contents of a file",
    functionParameters: BinaryData.FromString("""
    {
      "type": "object",
      "properties": {
        "file_path": {
          "type": "string",
          "description": "The path to the file to read"
        }
      },
      "required": ["file_path"]
    }
    """)
);

ChatCompletionOptions chatCompletionOptions = new()
{
    Tools = { readTool }
};

var client = new ChatClient(
    model: "anthropic/claude-haiku-4.5",
    credential: new ApiKeyCredential(apiKey),
    options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
);

ChatCompletion response = client.CompleteChat(
    [new UserChatMessage(prompt)],
    chatCompletionOptions
);

while (response.ToolCalls != null && response.ToolCalls.Count > 0) {
  messages.Add(new AssistantChatMessage(response));
  foreach (var call in response.ToolCalls) {

    string toolResponse = _ExecuteTool(call);
    ToolChatMessage toolMessage = ChatMessage.CreateToolMessage(
        toolCallId: call.Id, content: toolResponse);
    messages.Add(toolMessage);
  }

  response = client.CompleteChat(messages, options);
}

if (response.ToolCalls is { Count: > 0 })
{
    foreach (var toolCall in response.ToolCalls)
    {
        if (toolCall.FunctionName == "Read")
        {
            using var doc = JsonDocument.Parse(toolCall.FunctionArguments);
            if (doc.RootElement.TryGetProperty("file_path", out var pathProp))
            {
                var filePath = pathProp.GetString();
                if (!string.IsNullOrEmpty(filePath))
                {
                    Console.Write(File.ReadAllText(filePath));
                }
            }
        }
    }
}
else if (response.Content is { Count: > 0 })
{
    Console.Write(response.Content[0].Text);
}

