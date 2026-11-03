using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
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

ChatTool bashTool = ChatTool.CreateFunctionTool(
    functionName: "Bash",
    functionDescription: "Execute a shell command",
    functionParameters: BinaryData.FromString("""
    {
      "type": "object",
      "required": ["command"],
      "properties": {
        "command": {
          "type": "string",
          "description": "The command to execute"
        }
      }
    }
    """)
);

ChatTool skillTool = ChatTool.CreateFunctionTool(
    functionName: "Skill",
    functionDescription: "Load a skill's instructions into the conversation",
    functionParameters: BinaryData.FromString("""
    {
      "type": "object",
      "required": ["name"],
      "properties": {
        "name": {
          "type": "string",
          "description": "The name of the skill to use"
        },
        "args": {
          "type": "string",
          "description": "Optional arguments for the skill"
        }
      }
    }
    """)
);

ChatCompletionOptions chatCompletionOptions = new()
{
    Tools = { readTool, writeTool, bashTool, skillTool }
};

var client = new ChatClient(
    model: "anthropic/claude-haiku-4.5",
    credential: new ApiKeyCredential(apiKey),
    options: new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }
);

List<ChatMessage> messages = [];

var skillsDir = ".claude/skills";
if (Directory.Exists(skillsDir))
{
    var skillEntries = new List<string>();
    foreach (var dir in Directory.GetDirectories(skillsDir))
    {
        var skillFile = Path.Combine(dir, "SKILL.md");
        if (File.Exists(skillFile))
        {
            var (name, description, _) = ParseSkillFile(skillFile);
            skillEntries.Add($"- {name}: {description}");
        }
    }

    if (skillEntries.Count > 0)
    {
        messages.Add(new SystemChatMessage(
            "You have access to the following skills:\n\n" +
            string.Join("\n", skillEntries) +
            "\n\nIf a skill matches the user's request, call the Skill tool with its name and follow the instructions it returns."
        ));
    }
}

var remaining = prompt.TrimStart();
var expandedSkills = new List<(string Name, string Folder, string Body)>();

while (remaining.StartsWith("/"))
{
    var parts = remaining.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
    var skillName = parts[0][1..];
    var skillFile = Path.Combine(skillsDir, skillName, "SKILL.md");

    if (!string.IsNullOrEmpty(skillName) && File.Exists(skillFile))
    {
        var (name, _, body) = ParseSkillFile(skillFile);
        expandedSkills.Add((name, $"{skillsDir}/{skillName}", body));
        remaining = parts.Length > 1 ? parts[1].TrimStart() : "";
    }
    else
    {
        break;
    }
}

if (expandedSkills.Count > 0)
{
    var rawArgs = remaining.Trim();

    foreach (var (name, folder, body) in expandedSkills)
    {
        var messageContent = FormatSkillInstructions(name, folder, body, rawArgs);
        messages.Add(new UserChatMessage(messageContent));
    }
}
else
{
    messages.Add(new UserChatMessage(prompt));
}

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
        else if (call.FunctionName == "Bash")
        {
            using var doc = JsonDocument.Parse(call.FunctionArguments);
            if (doc.RootElement.TryGetProperty("command", out var commandProp))
            {
                var command = commandProp.GetString();
                if (!string.IsNullOrEmpty(command))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
                    psi.ArgumentList.Add(command);

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        var stdoutTask = process.StandardOutput.ReadToEndAsync();
                        var stderrTask = process.StandardError.ReadToEndAsync();
                        process.WaitForExit();
                        toolResponse = stdoutTask.Result + stderrTask.Result;
                    }
                }
            }
        }
        else if (call.FunctionName == "Skill")
        {
            using var doc = JsonDocument.Parse(call.FunctionArguments);
            if (doc.RootElement.TryGetProperty("name", out var nameProp))
            {
                var skillName = nameProp.GetString();
                var rawArgs = doc.RootElement.TryGetProperty("args", out var argsProp)
                    ? (argsProp.GetString()?.Trim() ?? "")
                    : "";

                if (!string.IsNullOrEmpty(skillName))
                {
                    var skillFile = Path.Combine(skillsDir, skillName, "SKILL.md");
                    if (File.Exists(skillFile))
                    {
                        var (name, _, body) = ParseSkillFile(skillFile);
                        toolResponse = FormatSkillInstructions(name, $"{skillsDir}/{skillName}", body, rawArgs);
                    }
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

static (string Name, string Description, string Body) ParseSkillFile(string filePath)
{
    var content = File.ReadAllText(filePath);
    var name = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";
    var description = "";
    var body = content.Trim();

    var normalized = content.Replace("\r\n", "\n");
    if (normalized.StartsWith("---\n"))
    {
        var closingIndex = normalized.IndexOf("\n---", 4);
        if (closingIndex != -1)
        {
            var frontmatter = normalized[4..closingIndex];
            body = normalized[(closingIndex + 4)..].Trim();

            foreach (var line in frontmatter.Split('\n'))
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex != -1)
                {
                    var key = line[..colonIndex].Trim();
                    var value = line[(colonIndex + 1)..].Trim();
                    if (key == "name") name = value;
                    else if (key == "description") description = value;
                }
            }
        }
    }

    return (name, description, body);
}

static string FormatSkillInstructions(string name, string folder, string body, string rawArgs)
{
    var argList = rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    var substituted = Regex.Replace(body, @"\$ARGUMENTS\[(\d+)\]|(\$ARGUMENTS)|\$(\d+)", match =>
    {
        if (match.Groups[1].Success)
        {
            int index = int.Parse(match.Groups[1].Value);
            return index < argList.Length ? argList[index] : "";
        }
        if (match.Groups[2].Success)
        {
            return rawArgs;
        }
        if (match.Groups[3].Success)
        {
            int index = int.Parse(match.Groups[3].Value);
            return index < argList.Length ? argList[index] : "";
        }
        return "";
    });

    return $"Skill: {name} (located at {folder})\n" +
           $"Paths in the instructions below are relative to that folder.\n\n" +
           substituted;
}
