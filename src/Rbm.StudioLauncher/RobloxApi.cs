using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rbm.StudioLauncher;

public sealed class GameInfo
{
    public long UniverseId { get; set; }
    public long RootPlaceId { get; set; }
    public string Name { get; set; } = "";
    public long GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public string Privacy { get; set; } = "";
    public DateTimeOffset? Updated { get; set; }
    public string? IconUrl { get; set; }
}

public sealed class GroupInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? IconUrl { get; set; }
    public string? Role { get; set; }
}

/// <summary>
/// APIs públicas do Roblox (sem login, sem cookie): lista de jogos de grupos (inclui privados),
/// dados de universos, ícones, grupos de um usuário. Nada de credenciais é lido ou enviado.
/// </summary>
public sealed class RobloxApi
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static RobloxApi() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("RBMPlaces/1.0");

    private sealed record Page<T>(List<T> Data, string? NextPageCursor);
    private sealed record GroupGame(long Id, string Name, GroupGameRoot? RootPlace, DateTimeOffset? Updated);
    private sealed record GroupGameRoot(long Id);
    private sealed record Universe(long Id, string Name, long RootPlaceId, string PrivacyType, string CreatorType, long CreatorTargetId, string CreatorName, DateTimeOffset? Updated);
    private sealed record UniverseOfPlace(long? UniverseId);
    private sealed record Thumb(long TargetId, string State, string? ImageUrl);
    private sealed record UserGroupRole(UserGroup Group, UserRole Role);
    private sealed record UserGroup(long Id, string Name);
    private sealed record UserRole(string Name, int Rank);
    private sealed record UserLookup(List<UserLookupItem> Data);
    private sealed record UserLookupItem(long Id, string Name, string DisplayName);
    private sealed record GroupV1(long Id, string Name);

    /// <summary>Todos os jogos de um grupo (públicos e privados), mais recentes primeiro.</summary>
    public async Task<List<GameInfo>> GetGroupGamesAsync(long groupId, string groupName, CancellationToken ct = default)
    {
        var list = new List<GameInfo>();
        string? cursor = null;
        do
        {
            var url = $"https://games.roblox.com/v2/groups/{groupId}/games?accessFilter=All&limit=50&sortOrder=Desc" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await Http.GetFromJsonAsync<Page<GroupGame>>(url, Json, ct).ConfigureAwait(false);
            if (page is null) break;
            list.AddRange(page.Data.Where(g => g.RootPlace is not null).Select(g => new GameInfo
            {
                UniverseId = g.Id, Name = g.Name.Trim(), RootPlaceId = g.RootPlace!.Id, GroupId = groupId, GroupName = groupName, Updated = g.Updated,
            }));
            cursor = page.NextPageCursor;
        } while (cursor is not null && list.Count < 500);
        return list;
    }

    public async Task<long?> GetUniverseIdAsync(long placeId, CancellationToken ct = default)
    {
        try { return (await Http.GetFromJsonAsync<UniverseOfPlace>($"https://apis.roblox.com/universes/v1/places/{placeId}/universe", Json, ct).ConfigureAwait(false))?.UniverseId; }
        catch { return null; }
    }

    public async Task<GameInfo?> GetUniverseAsync(long universeId, CancellationToken ct = default)
    {
        try
        {
            var u = await Http.GetFromJsonAsync<Universe>($"https://develop.roblox.com/v1/universes/{universeId}", Json, ct).ConfigureAwait(false);
            if (u is null) return null;
            return new GameInfo
            {
                UniverseId = u.Id, Name = u.Name.Trim(), RootPlaceId = u.RootPlaceId, Privacy = u.PrivacyType, Updated = u.Updated,
                GroupId = u.CreatorType == "Group" ? u.CreatorTargetId : 0, GroupName = u.CreatorType == "Group" ? u.CreatorName : "",
            };
        }
        catch { return null; }
    }

    public async Task<Dictionary<long, string>> GetGameIconsAsync(IEnumerable<long> universeIds, CancellationToken ct = default) =>
        await GetThumbsAsync("games/icons?universeIds", universeIds, ct).ConfigureAwait(false);

    public async Task<Dictionary<long, string>> GetGroupIconsAsync(IEnumerable<long> groupIds, CancellationToken ct = default) =>
        await GetThumbsAsync("groups/icons?groupIds", groupIds, ct).ConfigureAwait(false);

    private static async Task<Dictionary<long, string>> GetThumbsAsync(string kind, IEnumerable<long> ids, CancellationToken ct)
    {
        var map = new Dictionary<long, string>();
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            try
            {
                var url = $"https://thumbnails.roblox.com/v1/{kind}={string.Join(',', chunk)}&size=150x150&format=Png&isCircular=false";
                var page = await Http.GetFromJsonAsync<Page<Thumb>>(url, Json, ct).ConfigureAwait(false);
                foreach (var t in page?.Data ?? new()) if (t.State == "Completed" && t.ImageUrl is not null) map[t.TargetId] = t.ImageUrl;
            }
            catch { /* ícones são opcionais */ }
        }
        return map;
    }

    public async Task<List<GroupInfo>> GetUserGroupsAsync(long userId, CancellationToken ct = default)
    {
        var page = await Http.GetFromJsonAsync<Page<UserGroupRole>>($"https://groups.roblox.com/v2/users/{userId}/groups/roles", Json, ct).ConfigureAwait(false);
        return page?.Data.Select(r => new GroupInfo { Id = r.Group.Id, Name = r.Group.Name, Role = r.Role.Name }).ToList() ?? new();
    }

    public async Task<GroupInfo?> GetGroupAsync(long groupId, CancellationToken ct = default)
    {
        try
        {
            var g = await Http.GetFromJsonAsync<GroupV1>($"https://groups.roblox.com/v1/groups/{groupId}", Json, ct).ConfigureAwait(false);
            return g is null ? null : new GroupInfo { Id = g.Id, Name = g.Name };
        }
        catch { return null; }
    }

    /// <summary>Nome de usuário → (id, nome, apelido).</summary>
    public async Task<(long id, string name, string display)?> ResolveUsernameAsync(string username, CancellationToken ct = default)
    {
        var resp = await Http.PostAsJsonAsync("https://users.roblox.com/v1/usernames/users", new { usernames = new[] { username.Trim() }, excludeBannedUsers = true }, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var r = await resp.Content.ReadFromJsonAsync<UserLookup>(Json, ct).ConfigureAwait(false);
        var u = r?.Data.FirstOrDefault();
        return u is null ? null : (u.Id, u.Name, u.DisplayName);
    }
}
