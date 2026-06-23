using System.Text.Json.Serialization;

namespace GithubBot.Models;

public class Repository
{
    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
}
