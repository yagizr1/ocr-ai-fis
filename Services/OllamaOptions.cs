namespace FiscOCRApp.Services;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434/";
    public string Model { get; set; } = "qwen2.5:7b";
    public int TimeoutSeconds { get; set; } = 180;
}
