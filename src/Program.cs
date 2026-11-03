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

ChatTool writeTool = ChatTool.CreateFunctionTool(
    functionName: "Write",
    functionDescription: "Write content to a file",
    functionParameters: BinaryData.FromString("""
    {
      "type": "object",
      "required": ["file_path", "content"],
      "properties": {
        "file_path": {
          "type": "string",
          "description": "The path of the file to write to"
        },
        "content": {
          "type": "string",
          "description": "The content to write to the file"
        }
      }
    }
    """)
);

ChatCompletionOptions chatCompletionOptions = new()
{
    Tools = { readTool, writeTool }
};

var client = new ChatClient(
    model: "anthropic/claude-haiku-4.5",
    credential: new ApiKeyCredential(apiKey),
    options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
);

List<ChatMessage> messages = [new UserChatMessage(prompt)];

ChatCompletion response = client.CompleteChat(messages, chatCompletionOptions);

while (response.ToolCalls is { Count: > 0 })
{
    messages.Add(new AssistantChatMessage(response));

    foreach (var call in response.ToolCalls)
    {
        string toolResponse = "";

        if (call.FunctionName == "Read")
        {
            using var doc = JsonDocument.Parse(call.FunctionArguments);
            if (doc.RootElement.TryGetProperty("file_path", out var pathProp))
            {
                var filePath = pathProp.GetString();
                if (!string.IsNullOrEmpty(filePath))
                {
                    toolResponse = File.ReadAllText(filePath);
                }
            }
        }
        else if (call.FunctionName == "Write")
        {
            using var doc = JsonDocument.Parse(call.FunctionArguments);
            if (doc.RootElement.TryGetProperty("file_path", out var pathProp) &&
                doc.RootElement.TryGetProperty("content", out var contentProp))
            {
                var filePath = pathProp.GetString();
                var content = contentProp.GetString() ?? "";
                if (!string.IsNullOrEmpty(filePath))
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.WriteAllText(filePath, content);
                    toolResponse = "File written successfully";
                }
            }
        }

        ToolChatMessage toolMessage = ChatMessage.CreateToolMessage(
            toolCallId: call.Id,
            content: toolResponse
        );
        messages.Add(toolMessage);
    }

    response = client.CompleteChat(messages, chatCompletionOptions);
}

if (response.Content is { Count: > 0 })
{
    Console.Write(response.Content[0].Text);
}
