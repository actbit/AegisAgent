namespace AegisAgent.Core;

public static class ProviderKinds
{
    public const string OpenAi = "openai";
    public const string DeepSeek = "deepseek";
    public const string OpenAiCompatible = "openai-compatible";
    public const string Ollama = "ollama";
    public const string LmStudio = "lm-studio";
    public const string LlamaCpp = "llama-cpp";
    public const string Vllm = "vllm";
    public const string OpenRouter = "openrouter";
    public const string Anthropic = "anthropic";
    public const string ChatGptOAuth = "openai-chatgpt-oauth";

    public static bool IsLocal(string? kind) => kind?.ToLowerInvariant() switch
    {
        Ollama or LmStudio or LlamaCpp or Vllm => true,
        _ => false,
    };

    public static string? DefaultBaseUrl(string? kind) => kind?.ToLowerInvariant() switch
    {
        Ollama => "http://localhost:11434/v1",
        LmStudio => "http://localhost:1234/v1",
        LlamaCpp => "http://localhost:8080/v1",
        Vllm => "http://localhost:8000/v1",
        OpenRouter => "https://openrouter.ai/api/v1",
        DeepSeek => "https://api.deepseek.com/v1",
        Anthropic => "https://api.anthropic.com",
        _ => null,
    };

    public static string DefaultModel(string? kind) => kind?.ToLowerInvariant() switch
    {
        Ollama => "local-model",
        LmStudio => "local-model",
        LlamaCpp => "local-model",
        Vllm => "local-model",
        OpenRouter => "openai/gpt-oss-120b",
        Anthropic => "claude-sonnet-4-5",
        DeepSeek => "deepseek-chat",
        _ => "gpt-4.1-mini",
    };

    public static string DisplayName(string? kind) => kind?.ToLowerInvariant() switch
    {
        Ollama => "Ollama",
        LmStudio => "LM Studio",
        LlamaCpp => "llama.cpp",
        Vllm => "vLLM",
        OpenRouter => "OpenRouter",
        Anthropic => "Anthropic",
        OpenAiCompatible => "OpenAI-compatible",
        DeepSeek => "DeepSeek",
        ChatGptOAuth => "ChatGPT OAuth",
        _ => "OpenAI API",
    };
}
