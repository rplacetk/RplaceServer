using System.Text.Json.Serialization;

namespace HTTPOfficial.DataModel;

public class Post
{
    public int Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public int Upvotes { get; set; }
    public int Downvotes { get; set; }
    public DateTime CreationDate { get; set; }
    public bool HasSensitiveContent { get; set; }

	// ContentUploadKey should never be exposed in the API - Handle carefully
    [JsonIgnore]
    public string? ContentUploadKey { get; set; }

    // Navigation property to post contents
    public List<PostContent> Contents { get; set; } = [];

    // Either canvas user or Author is used depending on if post was created
    // under a global auth server account or a linked user
    public int? CanvasUserId { get; set; }
    // Navigation property to canvas user
    [JsonIgnore]
    public CanvasUser? CanvasUser { get; set; }

    public int? AccountId { get; set; }
    // Navigation property to account owner
    [JsonIgnore]
    public Account? Account { get; set; }

    public Post() { }

    public Post(string title, string description)
    {
        Title = title;
        Description = description;
    }
}
