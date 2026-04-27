using System.Text.Json.Serialization;

namespace ContextKing.Core.Knowledge;

public sealed record KnowledgeSnippet
{
    [JsonPropertyName("id")]         public required string Id { get; init; }
    [JsonPropertyName("content")]    public required string Content { get; init; }
    [JsonPropertyName("tags")]       public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("folders")]    public IReadOnlyList<string> Folders { get; init; } = [];
    [JsonPropertyName("source")]     public string Source { get; init; } = "agent";
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }
}
