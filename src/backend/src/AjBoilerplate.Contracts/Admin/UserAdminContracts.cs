using System.Text.Json.Serialization;

namespace AjBoilerplate.Contracts.Admin;

/// <summary>A person in the identity directory, as the Auth service reports them.</summary>
public sealed record DirectoryUserResponse(
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("firstName")] string? FirstName,
    [property: JsonPropertyName("lastName")] string? LastName,
    [property: JsonPropertyName("enabled")] bool Enabled);

/// <summary>Result of <c>GET /api/v1/admin/users?q=</c>. A blank <c>q</c> answers an empty list.</summary>
public sealed record DirectoryUsersResponse([property: JsonPropertyName("users")] IReadOnlyList<DirectoryUserResponse> Users);

/// <summary>A seat group the Auth service manages. <c>Path</c> is what <c>PUT …/groups</c> takes.</summary>
public sealed record DirectoryGroupResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("parentPath")] string? ParentPath);

/// <summary>Coverage of one managed portal in a directory read: <c>Outcome</c> is the name of an
/// <c>Success</c>, <c>Unavailable</c>, <c>Denied</c>, … outcome. Anything other than <c>Success</c> means that
/// portal's rows are missing from the answer — not that nobody holds a seat there.</summary>
public sealed record PortalAccessResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("outcome")] string Outcome);

public sealed record DirectoryGroupsResponse(
    [property: JsonPropertyName("groups")] IReadOnlyList<DirectoryGroupResponse> Groups,
    [property: JsonPropertyName("portals")] IReadOnlyList<PortalAccessResponse> Portals);

public sealed record DirectoryMemberResponse(
    [property: JsonPropertyName("user")] DirectoryUserResponse User,
    [property: JsonPropertyName("groups")] IReadOnlyList<string> Groups);

public sealed record DirectoryMembersResponse(
    [property: JsonPropertyName("members")] IReadOnlyList<DirectoryMemberResponse> Members,
    [property: JsonPropertyName("portals")] IReadOnlyList<PortalAccessResponse> Portals);

/// <summary>The groups a person holds, and what a write changed.</summary>
public sealed record UserGroupsResponse(
    [property: JsonPropertyName("groups")] IReadOnlyList<string> Groups,
    [property: JsonPropertyName("added")] IReadOnlyList<string> Added,
    [property: JsonPropertyName("removed")] IReadOnlyList<string> Removed,
    [property: JsonPropertyName("portals")] IReadOnlyList<PortalAccessResponse> Portals);

/// <summary><c>PUT /api/v1/admin/users/{subject}/groups</c>. The full set the person should hold; an empty
/// array removes every seat.</summary>
public sealed class SetUserGroupsRequest
{
    [JsonPropertyName("groups")]
    public IReadOnlyList<string>? Groups { get; set; }
}

/// <summary><c>POST /api/v1/admin/users</c>: give a person a seat, creating the account when the directory
/// has none. <c>Email</c> and <c>GroupPath</c> are required.</summary>
public sealed class CreateDirectoryUserRequest
{
    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("groupPath")]
    public string? GroupPath { get; set; }
}

public sealed record CreatedDirectoryUserResponse(
    [property: JsonPropertyName("user")] DirectoryUserResponse User,
    [property: JsonPropertyName("groups")] IReadOnlyList<string> Groups);
