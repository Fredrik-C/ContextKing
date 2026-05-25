using System.Text.Json.Serialization;

namespace ContextKing.Core.Knowledge;

public sealed record KnowledgeValidity
{
    [JsonPropertyName("status")]       public string Status { get; init; } = "unknown";
    [JsonPropertyName("validated_at")] public string? ValidatedAt { get; init; }
    [JsonPropertyName("confidence")]   public float? Confidence { get; init; }
}

public sealed record KnowledgeAnchors
{
    [JsonPropertyName("files")]   public IReadOnlyList<string> Files { get; init; } = [];
    [JsonPropertyName("symbols")] public IReadOnlyList<string> Symbols { get; init; } = [];
}

public sealed record KnowledgeFingerprints
{
    [JsonPropertyName("semantic_scope_hash")] public string? SemanticScopeHash { get; init; }
    [JsonPropertyName("anchor_signature_hash")] public string? AnchorSignatureHash { get; init; }
    [JsonPropertyName("context_hash")] public string? ContextHash { get; init; }
}

public sealed record KnowledgeOrigin
{
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("head")]   public string? Head { get; init; }
}

public sealed record KnowledgeSnippet
{
    [JsonPropertyName("id")]         public required string Id { get; init; }
    [JsonPropertyName("content")]    public required string Content { get; init; }
    [JsonPropertyName("tags")]       public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("folders")]    public IReadOnlyList<string> Folders { get; init; } = [];
    [JsonPropertyName("source")]     public string Source { get; init; } = "agent";
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    [JsonPropertyName("created_at")] public required string CreatedAt { get; init; }
    [JsonPropertyName("schema_version")] public int? SchemaVersion { get; init; }
    [JsonPropertyName("validity")] public KnowledgeValidity? Validity { get; init; }
    [JsonPropertyName("anchors")] public KnowledgeAnchors? Anchors { get; init; }
    [JsonPropertyName("fingerprints")] public KnowledgeFingerprints? Fingerprints { get; init; }
    [JsonPropertyName("origin")] public KnowledgeOrigin? Origin { get; init; }
}
