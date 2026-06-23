using System.Text.Json.Serialization;

namespace GithubBot.Models;

public class Repository
{
    [JsonPropertyName("name")]      public string Name     { get; set; } = "";
    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
}
