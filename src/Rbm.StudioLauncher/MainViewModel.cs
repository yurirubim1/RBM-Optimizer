using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.StudioLauncher;

public abstract partial class TileBase : ObservableObject
{
    public abstract string Name { get; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string? _icon;
    [ObservableProperty] private string _subtitle = "";
}

/// <summary>Jogo do Roblox (abre no Studio).</summary>
public sealed partial class GameTile : TileBase
{
    public required GameInfo Game { get; init; }
    public override string Name => Game.Name;
    public string Group => string.IsNullOrEmpty(Game.GroupName) ? "Pessoal" : Game.GroupName;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private FolderTile? _linkedFolder;
    public DateTime? LastOpened { get; set; }
}

/// <summary>Pasta de projeto (abre no VS Code).</summary>
public sealed partial class FolderTile : TileBase
{
    public required string Path { get; init; }
    public override string Name => System.IO.Path.GetFileName(Path);
    public DateTime Modified { get; init; }
    [ObservableProperty] private GameTile? _linkedGame;
}

public sealed partial class NavEntry : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Glyph { get; init; } = "";
    public long GroupId { get; init; }
    [ObservableProperty] private string? _icon;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isActive;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly RobloxApi _api = new();
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly Dictionary<long, GameTile> _games = new();                     // por universeId
    private readonly Dictionary<string, FolderTile> _folders = new(StringComparer.OrdinalIgnoreCase); // por caminho
    private readonly Dictionary<long, List<long>> _groupGames = new();
    private List<long> _sessionUniverses = new();
    private List<string> _codeSession = new();
    private readonly DispatcherTimer _openTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _openCts;

    public ObservableCollection<NavEntry> Sections { get; } = new();
    public ObservableCollection<NavEntry> CodeSections { get; } = new();
    public ObservableCollection<NavEntry> Groups { get; } = new();
    public ObservableCollection<TileBase> Tiles { get; } = new();
    public ICollectionView TilesView { get; }

    [ObservableProperty] private NavEntry? _current;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _title = "Recentes";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isOpening;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _openProgress;
    [ObservableProperty] private int _delaySeconds;
    [ObservableProperty] private int _runningStudios;
    [ObservableProperty] private int _openCodeWindows;
    [ObservableProperty] private bool _openTogether;
    [ObservableProperty] private string _accountText = "Nenhuma conta informada";
    [ObservableProperty] private bool _showSettings;
    [ObservableProperty] private string _accountInput = "";
    [ObservableProperty] private string _groupInput = "";
    [ObservableProperty] private string _projectsRoot = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _codeAvailable;

    public MainViewModel()
    {
        TilesView = CollectionViewSource.GetDefaultView(Tiles);
        TilesView.Filter = o => string.IsNullOrWhiteSpace(Search) || ((TileBase)o).Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
        DelaySeconds = _settings.OpenDelaySeconds;
        OpenTogether = _settings.OpenTogether;
        AccountInput = _settings.Username ?? "";
        AccountText = _settings.Username is { } u ? $"Conta: {u}" : "Nenhuma conta informada";
        ProjectsRoot = _settings.ProjectsRoot ?? VsCodeService.DefaultProjectsRoot() ?? "";
        CodeAvailable = VsCodeService.CodeExe is not null;
        Sections.Add(new NavEntry { Key = "recent", Title = "Recentes", Glyph = "" });
        Sections.Add(new NavEntry { Key = "session", Title = "Última sessão", Glyph = "" });
        Sections.Add(new NavEntry { Key = "favorites", Title = "Favoritos", Glyph = "" });
        CodeSections.Add(new NavEntry { Key = "folders", Title = "Pastas de projetos", Glyph = "" });
        CodeSections.Add(new NavEntry { Key = "codesession", Title = "Última sessão do VS Code", Glyph = "" });
        _openTimer.Tick += (_, _) => RefreshOpenState();
        _openTimer.Start();
        StartChats();
        _ = LoadAsync();
    }

    partial void OnSearchChanged(string value) { TilesView.Refresh(); UpdateEmpty(); }
    partial void OnDelaySecondsChanged(int value) { _settings.OpenDelaySeconds = Math.Clamp(value, 2, 60); _settings.Save(); }
    partial void OnOpenTogetherChanged(bool value) { _settings.OpenTogether = value; _settings.Save(); }

    // ======================= Carregamento =======================

    private async Task LoadAsync()
    {
        IsLoading = true;
        Status = "Lendo seus projetos recentes…";
        try
        {
            var recent = await Task.Run(() => StudioService.Recent());
            var sessionPlaces = await Task.Run(StudioService.LastSession);

            // Recentes do Studio → universos → jogos.
            var placeToUniverse = new Dictionary<long, long>();
            foreach (var r in recent.Take(60))
            {
                if (_settings.UniverseOfPlaceCache.TryGetValue(r.PlaceId, out var c) && long.TryParse(c, out var cu)) { placeToUniverse[r.PlaceId] = cu; continue; }
                if (await _api.GetUniverseIdAsync(r.PlaceId) is { } u) { placeToUniverse[r.PlaceId] = u; _settings.UniverseOfPlaceCache[r.PlaceId] = u.ToString(); }
            }
            _settings.Save();
            foreach (var (placeId, universeId) in placeToUniverse)
            {
                if (!_games.ContainsKey(universeId) && await _api.GetUniverseAsync(universeId) is { } g) AddGame(g);
                if (_games.TryGetValue(universeId, out var t))
                {
                    var r = recent.First(x => x.PlaceId == placeId);
                    if (t.LastOpened is null || r.LastOpened > t.LastOpened) t.LastOpened = r.LastOpened;
                }
            }
            _sessionUniverses = sessionPlaces.Where(placeToUniverse.ContainsKey).Select(p => placeToUniverse[p]).Distinct().ToList();

            // Grupos (dos recentes, da conta e adicionados à mão).
            var recentGroups = _games.Values.Select(t => t.Game.GroupId).Where(g => g != 0).Distinct().ToList();
            var groupInfos = new Dictionary<long, GroupInfo>();
            foreach (var t in _games.Values.Where(t => t.Game.GroupId != 0)) groupInfos[t.Game.GroupId] = new GroupInfo { Id = t.Game.GroupId, Name = t.Game.GroupName };
            if (_settings.UserId is { } uid)
            {
                try { foreach (var g in await _api.GetUserGroupsAsync(uid)) groupInfos.TryAdd(g.Id, g); }
                catch { Status = "Não foi possível carregar os grupos da conta (sem internet?)."; }
            }
            foreach (var gid in _settings.ExtraGroups.Where(g => !groupInfos.ContainsKey(g)))
                if (await _api.GetGroupAsync(gid) is { } gi) groupInfos[gid] = gi;

            // Jogos dos grupos em que você trabalha (os dos recentes) já são carregados para ligar às pastas.
            foreach (var gid in recentGroups) await LoadGroupGamesAsync(gid, groupInfos[gid].Name);

            var ordered = groupInfos.Values.OrderByDescending(g => recentGroups.Contains(g.Id)).ThenBy(g => g.Name).ToList();
            var gicons = await _api.GetGroupIconsAsync(ordered.Select(g => g.Id));
            Groups.Clear();
            foreach (var g in ordered)
                Groups.Add(new NavEntry { Key = $"group:{g.Id}", Title = g.Name, GroupId = g.Id, Icon = gicons.GetValueOrDefault(g.Id), Count = _groupGames.GetValueOrDefault(g.Id)?.Count ?? 0 });

            // Pastas do VS Code.
            LoadFolders();
            LinkFoldersToGames();
            await LoadIconsAsync(_games.Keys);

            RefreshOpenState();
            await Navigate(Sections[0]);
            Status = "";
        }
        catch (Exception ex) { Status = "Erro ao carregar: " + ex.Message; }
        finally { IsLoading = false; UpdateEmpty(); }
    }

    private async Task LoadGroupGamesAsync(long groupId, string name)
    {
        if (_groupGames.ContainsKey(groupId)) return;
        try
        {
            var games = await _api.GetGroupGamesAsync(groupId, name);
            _groupGames[groupId] = games.Select(g => AddGame(g).Game.UniverseId).ToList();
        }
        catch { _groupGames[groupId] = new(); throw; }
    }

    private GameTile AddGame(GameInfo g)
    {
        if (_games.TryGetValue(g.UniverseId, out var existing)) return existing;
        var t = new GameTile { Game = g, IsFavorite = _settings.Favorites.Contains(g.UniverseId) };
        Track(t);
        _games[g.UniverseId] = t;
        return t;
    }

    private void Track(TileBase t) =>
        t.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TileBase.IsSelected)) SelectedCount = _games.Values.Count(x => x.IsSelected) + _folders.Values.Count(x => x.IsSelected); };

    private void LoadFolders()
    {
        var paths = new List<(string path, DateTime mod)>();
        if (!string.IsNullOrWhiteSpace(ProjectsRoot))
            paths.AddRange(VsCodeService.ProjectFolders(ProjectsRoot).Select(d => (d.FullName, d.LastWriteTime)));
        _codeSession = VsCodeService.LastSessionFolders().Where(Directory.Exists).ToList();
        foreach (var p in _codeSession.Where(p => paths.All(x => !string.Equals(x.path, p, StringComparison.OrdinalIgnoreCase))))
            paths.Add((p, new DirectoryInfo(p).LastWriteTime));
        foreach (var (path, mod) in paths)
        {
            if (_folders.ContainsKey(path)) continue;
            var f = new FolderTile { Path = path, Modified = mod };
            Track(f);
            _folders[path] = f;
        }
    }

    /// <summary>Liga cada pasta ao jogo de nome mais parecido (ou à ligação escolhida pelo usuário).</summary>
    private void LinkFoldersToGames()
    {
        foreach (var f in _folders.Values)
        {
            GameTile? best = null;
            if (_settings.FolderLinks.TryGetValue(f.Name, out var forced)) best = forced == 0 ? null : _games.GetValueOrDefault(forced); // 0 = desvinculado pelo usuário
            else
            {
                double bestScore = 0;
                foreach (var g in _games.Values)
                {
                    var s = VsCodeService.Similarity(f.Name, g.Name);
                    if (s > bestScore) { bestScore = s; best = g; }
                }
                if (bestScore < 0.6) best = null;
            }
            f.LinkedGame = best;
            if (best is not null && (best.LinkedFolder is null || best.LinkedFolder.Modified < f.Modified)) best.LinkedFolder = f;
        }
    }

    private async Task LoadIconsAsync(IEnumerable<long> universeIds)
    {
        var missing = universeIds.Where(id => _games.TryGetValue(id, out var t) && t.Icon is null).ToList();
        if (missing.Count == 0) return;
        var icons = await _api.GetGameIconsAsync(missing);
        foreach (var (id, url) in icons) if (_games.TryGetValue(id, out var t)) t.Icon = url;
        foreach (var f in _folders.Values) f.Icon = f.LinkedGame?.Icon;
    }

    // ======================= Navegação =======================

    [RelayCommand]
    private async Task Navigate(NavEntry? entry)
    {
        if (entry is null) return;
        foreach (var e in Sections.Concat(CodeSections).Concat(Groups)) e.IsActive = e == entry;
        IsChatsView = false; ChatsEntry.IsActive = false;
        Current = entry;
        IEnumerable<TileBase> items;
        switch (entry.Key)
        {
            case "recent":
                Title = "Recentes"; Subtitle = "Places que você abriu no Studio nos últimos 45 dias";
                items = _games.Values.Where(t => t.LastOpened is not null).OrderByDescending(t => t.LastOpened);
                break;
            case "session":
                Title = "Última sessão do Studio"; Subtitle = "O que estava aberto da última vez — ideal depois de um travamento";
                items = _sessionUniverses.Select(u => _games.GetValueOrDefault(u)).OfType<GameTile>();
                break;
            case "favorites":
                Title = "Favoritos"; Subtitle = "Clique na estrela de um jogo para fixá-lo aqui";
                items = _games.Values.Where(t => t.IsFavorite).OrderBy(t => t.Name);
                break;
            case "folders":
                Title = "Pastas de projetos"; Subtitle = string.IsNullOrWhiteSpace(ProjectsRoot) ? "Escolha a pasta dos seus projetos em Configurações" : $"{ProjectsRoot} — cada pasta abre numa janela própria do VS Code";
                items = _folders.Values.Where(f => IsUnderRoot(f.Path)).OrderByDescending(f => f.Modified);
                break;
            case "codesession":
                Title = "Última sessão do VS Code"; Subtitle = "As janelas que estavam abertas no VS Code — reabra todas de uma vez";
                items = _codeSession.Select(p => _folders.GetValueOrDefault(p)).OfType<FolderTile>();
                break;
            default:
                Title = entry.Title; Subtitle = "Todos os jogos do grupo, incluindo os privados";
                if (!_groupGames.ContainsKey(entry.GroupId))
                {
                    IsLoading = true; Status = $"Carregando jogos de {entry.Title}…";
                    try { await LoadGroupGamesAsync(entry.GroupId, entry.Title); LinkFoldersToGames(); await LoadIconsAsync(_groupGames[entry.GroupId]); Status = ""; }
                    catch (Exception ex) { Status = "Não foi possível carregar os jogos do grupo: " + ex.Message; }
                    finally { IsLoading = false; }
                }
                items = _groupGames.GetValueOrDefault(entry.GroupId, new()).Select(u => _games[u]);
                entry.Count = _groupGames.GetValueOrDefault(entry.GroupId)?.Count ?? 0;
                break;
        }
        Tiles.Clear();
        foreach (var t in items) { t.Subtitle = SubtitleFor(t); Tiles.Add(t); }
        if (entry.GroupId == 0) entry.Count = Tiles.Count;
        UpdateEmpty();
    }

    private bool IsUnderRoot(string path) => !string.IsNullOrWhiteSpace(ProjectsRoot) && path.StartsWith(ProjectsRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private void UpdateEmpty()
    {
        IsEmpty = TilesView.IsEmpty && !IsLoading;
        EmptyText = !string.IsNullOrWhiteSpace(Search) ? "Nada encontrado com esse nome." : Current?.Key switch
        {
            "favorites" => "Nenhum favorito ainda. Clique na estrela de um jogo para fixá-lo aqui.",
            "session" => "Não encontramos a última sessão nos logs do Studio.",
            "recent" => "Nenhuma place aberta recentemente no Studio.",
            "folders" => "Nenhuma pasta encontrada. Escolha a pasta dos projetos em Configurações.",
            "codesession" => "O VS Code não tem janelas salvas da última sessão.",
            _ => "Este grupo não tem jogos (ou não foi possível carregá-los).",
        };
    }

    private static string SubtitleFor(TileBase t) => t switch
    {
        GameTile g when g.LastOpened is { } d => "Aberto no Studio " + Ago(d),
        GameTile g when g.Game.Updated is { } u => "Atualizado " + Ago(u.LocalDateTime),
        GameTile g => g.Group,
        FolderTile f => "Modificada " + Ago(f.Modified),
        _ => "",
    };

    private static string Ago(DateTime d)
    {
        var s = DateTime.Now - d;
        if (s.TotalMinutes < 1) return "agora";
        if (s.TotalHours < 1) return $"há {(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"há {(int)s.TotalHours} h";
        if (s.TotalDays < 2) return "ontem";
        if (s.TotalDays < 30) return $"há {(int)s.TotalDays} dias";
        return $"em {d:dd/MM/yyyy}";
    }

    private void RefreshOpenState()
    {
        var studioTitles = StudioService.OpenWindowTitles();
        var codeFolders = VsCodeService.OpenFolderNames();
        RunningStudios = StudioService.RunningStudios();
        OpenCodeWindows = codeFolders.Count;
        // Identificação exata pela place aberta em cada Studio (o título da janela vem traduzido pelo Roblox).
        var openPlaces = StudioService.OpenPlaceIds();
        var openUniverses = openPlaces.Select(p => _settings.UniverseOfPlaceCache.TryGetValue(p, out var u) && long.TryParse(u, out var uid) ? uid : 0).ToHashSet();
        foreach (var g in _games.Values)
            g.IsOpen = openPlaces.Contains(g.Game.RootPlaceId) || openUniverses.Contains(g.Game.UniverseId)
                       || studioTitles.Any(w => w.StartsWith(g.Name + " - ", StringComparison.OrdinalIgnoreCase));
        foreach (var f in _folders.Values) f.IsOpen = codeFolders.Contains(f.Name);
    }

    // ======================= Seleção =======================

    [RelayCommand]
    private void SelectAll()
    {
        var visible = TilesView.Cast<TileBase>().ToList();
        bool all = visible.All(t => t.IsSelected);
        foreach (var t in visible) t.IsSelected = !all;
    }

    [RelayCommand]
    private void ClearSelection() { foreach (var t in _games.Values.Cast<TileBase>().Concat(_folders.Values)) t.IsSelected = false; }

    [RelayCommand]
    private void ToggleFavorite(GameTile? t)
    {
        if (t is null) return;
        t.IsFavorite = !t.IsFavorite;
        if (t.IsFavorite) { if (!_settings.Favorites.Contains(t.Game.UniverseId)) _settings.Favorites.Add(t.Game.UniverseId); }
        else _settings.Favorites.Remove(t.Game.UniverseId);
        _settings.Save();
        Sections[2].Count = _games.Values.Count(x => x.IsFavorite);
        if (Current?.Key == "favorites") _ = Navigate(Current);
    }

    // ======================= Abrir =======================

    private sealed record OpenAction(string Label, bool IsStudio, Action Run);

    [RelayCommand]
    private Task OpenSelected() => OpenAsync(_games.Values.Cast<TileBase>().Concat(_folders.Values).Where(t => t.IsSelected).ToList());

    [RelayCommand]
    private Task OpenOne(TileBase? t) => t is null ? Task.CompletedTask : OpenAsync(new List<TileBase> { t });

    [RelayCommand]
    private async Task RestoreSession()
    {
        var list = _sessionUniverses.Select(u => _games.GetValueOrDefault(u)).OfType<TileBase>()
            .Concat(_codeSession.Select(p => _folders.GetValueOrDefault(p)).OfType<TileBase>()).ToList();
        await OpenAsync(list, together: false);
    }

    private async Task OpenAsync(List<TileBase> list, bool? together = null)
    {
        if (IsOpening) return;
        bool pair = together ?? OpenTogether;
        // Monta a lista de ações (sem duplicar e pulando o que já está aberto).
        var games = new List<GameTile>(); var folders = new List<FolderTile>();
        foreach (var t in list)
        {
            if (t is GameTile g) { games.Add(g); if (pair && g.LinkedFolder is { } lf) folders.Add(lf); }
            if (t is FolderTile f) { folders.Add(f); if (pair && f.LinkedGame is { } lg) games.Add(lg); }
        }
        games = games.Distinct().ToList(); folders = folders.Distinct().ToList();
        int skipped = games.Count(g => g.IsOpen) + folders.Count(f => f.IsOpen);
        var actions = new List<OpenAction>();
        actions.AddRange(folders.Where(f => !f.IsOpen).Select(f => new OpenAction($"VS Code: {f.Name}", false, () => VsCodeService.OpenFolder(f.Path))));
        actions.AddRange(games.Where(g => !g.IsOpen).Select(g => new OpenAction($"Studio: {g.Name}", true, () => StudioService.OpenPlace(g.Game.RootPlaceId, g.Game.UniverseId))));
        if (actions.Count == 0) { Status = skipped > 0 ? "Tudo isso já está aberto." : "Selecione pelo menos um item."; return; }

        IsOpening = true; OpenProgress = 0;
        _openCts = new CancellationTokenSource();
        try
        {
            for (int i = 0; i < actions.Count; i++)
            {
                var a = actions[i];
                Status = $"Abrindo {i + 1} de {actions.Count} — {a.Label}" + (skipped > 0 ? $"  ·  {skipped} já aberto(s)" : "");
                a.Run();
                OpenProgress = (i + 1) * 100.0 / actions.Count;
                if (i < actions.Count - 1)
                {
                    // Studio é pesado (placa de vídeo): espera o intervalo configurado. VS Code: só um respiro.
                    var wait = a.IsStudio || actions[i + 1].IsStudio ? Math.Clamp(DelaySeconds, 2, 60) : 1;
                    await Task.Delay(TimeSpan.FromSeconds(wait), _openCts.Token);
                }
            }
            foreach (var t in list) t.IsSelected = false;
            Status = $"Pronto: {actions.Count} janela(s) abertas." + (skipped > 0 ? $" {skipped} já estava(m) aberta(s)." : "");
        }
        catch (OperationCanceledException) { Status = "Abertura interrompida."; }
        catch (Exception ex) { Status = "Erro ao abrir: " + ex.Message; }
        finally { IsOpening = false; }
    }

    [RelayCommand]
    private void StopOpening() => _openCts?.Cancel();

    // ======================= Chats do Claude =======================

    public ObservableCollection<ChatSession> Chats { get; } = new();
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
    public NavEntry ChatsEntry { get; } = new() { Key = "chats", Title = "Chats do Claude", Glyph = "" };
    private readonly Dictionary<string, ChatSession> _chatByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _chatTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _chatsPrimed;

    [ObservableProperty] private ChatSession? _selectedChat;
    [ObservableProperty] private bool _isChatsView;
    [ObservableProperty] private int _working;
    [ObservableProperty] private int _needsYou;
    [ObservableProperty] private bool _chatNotifications;

    /// <summary>Disparado quando um Claude termina ou para esperando você (título, texto).</summary>
    public event Action<string, string>? Notify;

    private void StartChats()
    {
        ChatNotifications = _settings.ChatNotifications;
        _chatTimer.Tick += (_, _) => PollChats();
        _chatTimer.Start();
        PollChats();
    }

    partial void OnChatNotificationsChanged(bool value) { _settings.ChatNotifications = value; _settings.Save(); }

    partial void OnSelectedChatChanged(ChatSession? value)
    {
        ChatMessages.Clear();
        if (value is null) return;
        value.Unread = false;
        foreach (var m in value.Messages) ChatMessages.Add(m);
        UpdateChatCounts();
    }

    private void PollChats()
    {
        try
        {
            var files = ClaudeSessions.RecentFiles(3);
            var openFolders = VsCodeService.OpenFolderNames();
            foreach (var f in files)
            {
                if (!_chatByFile.TryGetValue(f.FullName, out var s))
                {
                    s = new ChatSession { File = f.FullName, SessionId = System.IO.Path.GetFileNameWithoutExtension(f.Name) };
                    _chatByFile[f.FullName] = s;
                }
                var before = s.State;
                ClaudeSessions.Update(s, f);
                s.WindowOpen = openFolders.Contains(s.Project);
                if (_chatsPrimed && before == ChatState.Working && s.State is ChatState.Done or ChatState.WaitingYou)
                {
                    if (SelectedChat != s || !IsChatsView) s.Unread = true;
                    if (ChatNotifications)
                        Notify?.Invoke(s.State == ChatState.Done ? $"Claude terminou — {s.Project}" : $"Claude precisa de você — {s.Project}",
                                       s.State == ChatState.Done ? (string.IsNullOrEmpty(s.LastAssistant) ? "A resposta está pronta." : s.LastAssistant) : "Provavelmente aguardando uma aprovação no VS Code.");
                }
                if (s == SelectedChat && ChatMessages.Count != s.Messages.Count) { ChatMessages.Clear(); foreach (var m in s.Messages) ChatMessages.Add(m); }
            }
            _chatsPrimed = true;
            // Ordena: precisa de você → trabalhando → terminou → parado; dentro, mais recente primeiro.
            // Uma conversa por projeto (a mais recente).
            var ordered = _chatByFile.Values.Where(s => !string.IsNullOrEmpty(s.Project))
                .GroupBy(s => s.Project, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(x => x.LastWrite).First())
                .OrderBy(s => s.State switch { ChatState.WaitingYou => 0, ChatState.Working => 1, ChatState.Done => 2, _ => 3 })
                .ThenByDescending(s => s.LastWrite).ToList();
            if (!ordered.SequenceEqual(Chats)) { var sel = SelectedChat; Chats.Clear(); foreach (var s in ordered) Chats.Add(s); SelectedChat = sel; }
            UpdateChatCounts();
        }
        catch { /* leitura de arquivos em uso: tenta de novo no próximo ciclo */ }
    }

    private void UpdateChatCounts()
    {
        Working = Chats.Count(c => c.State == ChatState.Working);
        NeedsYou = Chats.Count(c => c.State == ChatState.WaitingYou || c.Unread);
        ChatsEntry.Count = Working + NeedsYou;
    }

    [RelayCommand]
    private void ShowChats()
    {
        foreach (var e in Sections.Concat(CodeSections).Concat(Groups)) e.IsActive = false;
        ChatsEntry.IsActive = true;
        IsChatsView = true;
        Title = "Chats do Claude"; Subtitle = "Todas as conversas dos seus projetos — veja quem terminou e pule para a janela certa";
        SelectedChat ??= Chats.FirstOrDefault();
    }

    partial void OnCurrentChanged(NavEntry? value) { if (value is not null) { IsChatsView = false; ChatsEntry.IsActive = false; } }

    [RelayCommand]
    private void GoToChat(ChatSession? s)
    {
        s ??= SelectedChat;
        if (s is null) return;
        s.Unread = false; UpdateChatCounts();
        if (VsCodeService.FocusFolder(s.Project)) return;
        var folder = !string.IsNullOrEmpty(s.Cwd) && System.IO.Directory.Exists(s.Cwd) ? (_folders.Values.FirstOrDefault(f => f.Name.Equals(s.Project, StringComparison.OrdinalIgnoreCase))?.Path ?? s.Cwd) : null;
        if (folder is null) { Status = "A pasta desta conversa não existe mais."; return; }
        VsCodeService.OpenFolder(folder);
        Status = $"Abrindo {s.Project} no VS Code — a conversa fica no painel do Claude.";
    }

    // ======================= Vínculo jogo ↔ pasta =======================

    public ObservableCollection<GameTile> PickerGames { get; } = new();
    [ObservableProperty] private bool _showPicker;
    [ObservableProperty] private string _pickerSearch = "";
    [ObservableProperty] private string _pickerTitle = "";
    private FolderTile? _pickerFolder;

    [ObservableProperty] private bool _showNewFolder;
    [ObservableProperty] private string _newFolderName = "";
    [ObservableProperty] private bool _openNewFolderInCode = true;
    private GameTile? _newFolderGame;

    partial void OnPickerSearchChanged(string value) => FillPicker();

    private void FillPicker()
    {
        PickerGames.Clear();
        foreach (var g in _games.Values.Where(g => string.IsNullOrWhiteSpace(PickerSearch) || g.Name.Contains(PickerSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                                       .OrderByDescending(g => g.LastOpened ?? DateTime.MinValue).ThenBy(g => g.Name))
            PickerGames.Add(g);
    }

    /// <summary>Nome de pasta a partir do nome do jogo (sem emojis, tags entre colchetes e caracteres inválidos).</summary>
    public static string SuggestFolderName(string game)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(game, @"\[[^\]]*\]", " ");
        s = new string(s.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '+' or '!' or '\'' or '&' or '(' or ')' or '.' or ',').ToArray());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim(' ', '.');
        return s.Length == 0 ? "Novo projeto" : s;
    }

    private void SetLink(FolderTile f, GameTile? g)
    {
        // Remove vínculos antigos dos dois lados.
        if (f.LinkedGame is { } old && old.LinkedFolder == f) old.LinkedFolder = null;
        if (g?.LinkedFolder is { } oldF && oldF != f) { oldF.LinkedGame = null; _settings.FolderLinks[oldF.Name] = 0; }
        f.LinkedGame = g;
        if (g is not null) g.LinkedFolder = f;
        f.Icon = g?.Icon;
        _settings.FolderLinks[f.Name] = g?.Game.UniverseId ?? 0; // 0 = "sem vínculo" (desliga a ligação automática)
        _settings.Save();
        foreach (var t in Tiles) t.Subtitle = SubtitleFor(t);
    }

    /// <summary>Cria a pasta do projeto com o nome do jogo e já vincula.</summary>
    [RelayCommand]
    private void CreateFolderFor(GameTile? g)
    {
        if (g is null) return;
        if (string.IsNullOrWhiteSpace(ProjectsRoot)) { Status = "Escolha primeiro a pasta dos projetos em Configurações."; ShowSettings = true; return; }
        _newFolderGame = g;
        NewFolderName = SuggestFolderName(g.Name);
        ShowNewFolder = true;
    }

    [RelayCommand]
    private void ConfirmNewFolder()
    {
        var g = _newFolderGame; if (g is null) return;
        var name = NewFolderName.Trim();
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { Status = "Nome de pasta inválido."; return; }
        var path = System.IO.Path.Combine(ProjectsRoot, name);
        try
        {
            bool existed = Directory.Exists(path);
            Directory.CreateDirectory(path);
            if (!_folders.TryGetValue(path, out var f))
            {
                f = new FolderTile { Path = path, Modified = DateTime.Now };
                Track(f);
                _folders[path] = f;
            }
            SetLink(f, g);
            ShowNewFolder = false;
            Status = existed ? $"A pasta \"{name}\" já existia e foi vinculada a {g.Name}." : $"Pasta \"{name}\" criada e vinculada a {g.Name}.";
            if (OpenNewFolderInCode) VsCodeService.OpenFolder(path);
            CodeSections[0].Count = _folders.Values.Count(x => IsUnderRoot(x.Path));
        }
        catch (Exception ex) { Status = "Não foi possível criar a pasta: " + ex.Message; }
    }

    [RelayCommand]
    private void CancelDialogs() { ShowNewFolder = false; ShowPicker = false; }

    /// <summary>Vincula o jogo a uma pasta já existente (escolhida no Explorer).</summary>
    [RelayCommand]
    private void LinkFolderTo(GameTile? g)
    {
        if (g is null) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Pasta do projeto de \"{g.Name}\"", InitialDirectory = Directory.Exists(ProjectsRoot) ? ProjectsRoot : null };
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FolderName.TrimEnd('\\');
        if (!_folders.TryGetValue(path, out var f))
        {
            f = new FolderTile { Path = path, Modified = new DirectoryInfo(path).LastWriteTime };
            Track(f);
            _folders[path] = f;
        }
        SetLink(f, g);
        Status = $"{g.Name} ↔ {f.Name} vinculados.";
    }

    /// <summary>Abre a lista de jogos para escolher a qual vincular esta pasta.</summary>
    [RelayCommand]
    private void LinkGameTo(FolderTile? f)
    {
        if (f is null) return;
        _pickerFolder = f;
        PickerTitle = $"Qual jogo do Roblox é \"{f.Name}\"?";
        PickerSearch = "";
        FillPicker();
        ShowPicker = true;
    }

    [RelayCommand]
    private void PickGame(GameTile? g)
    {
        if (_pickerFolder is null || g is null) return;
        SetLink(_pickerFolder, g);
        ShowPicker = false;
        Status = $"{_pickerFolder.Name} ↔ {g.Name} vinculados.";
    }

    [RelayCommand]
    private void Unlink(TileBase? t)
    {
        var f = t as FolderTile ?? (t as GameTile)?.LinkedFolder;
        if (f is null) return;
        var g = f.LinkedGame;
        SetLink(f, null);
        Status = g is null ? "Vínculo removido." : $"{f.Name} não está mais vinculada a {g.Name}.";
    }

    // ======================= Configurações =======================

    [RelayCommand]
    private void ToggleSettings() => ShowSettings = !ShowSettings;

    [RelayCommand]
    private async Task SaveAccount()
    {
        var name = AccountInput.Trim();
        if (name.Length == 0) { _settings.Username = null; _settings.UserId = null; _settings.Save(); AccountText = "Nenhuma conta informada"; await ReloadAsync(); return; }
        Status = "Procurando a conta…";
        var u = await _api.ResolveUsernameAsync(name);
        if (u is null) { Status = $"Conta \"{name}\" não encontrada."; return; }
        _settings.Username = u.Value.name; _settings.UserId = u.Value.id; _settings.Save();
        AccountText = $"Conta: {u.Value.name}";
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task AddGroup()
    {
        var m = System.Text.RegularExpressions.Regex.Match(GroupInput, @"(\d{3,})");
        if (!m.Success) { Status = "Cole o link ou o número (ID) do grupo."; return; }
        var id = long.Parse(m.Value);
        if (!_settings.ExtraGroups.Contains(id)) { _settings.ExtraGroups.Add(id); _settings.Save(); }
        GroupInput = "";
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ChooseProjectsRoot()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta onde ficam seus projetos", InitialDirectory = ProjectsRoot };
        if (dlg.ShowDialog() != true) return;
        ProjectsRoot = dlg.FolderName;
        _settings.ProjectsRoot = ProjectsRoot; _settings.Save();
        await ReloadAsync();
    }

    [RelayCommand]
    private Task Reload() => ReloadAsync();

    private async Task ReloadAsync()
    {
        var key = Current?.Key;
        _groupGames.Clear(); _games.Clear(); _folders.Clear();
        await LoadAsync();
        var target = Sections.Concat(CodeSections).Concat(Groups).FirstOrDefault(e => e.Key == key);
        if (target is not null) await Navigate(target);
    }
}
