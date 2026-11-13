using Rbm.Core;
using Rbm.Optimization.Cleanup;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rbm.UI.Pages.SystemPages;

/// <summary>Linha da página de Limpeza: uma categoria da allow-list com o último resultado (análise ou limpeza).</summary>
public sealed partial class CleanupItem : ObservableObject
{
    public CleanupItem(CleanupCategory c) => Category = c;

    public CleanupCategory Category { get; }
    public string Id => Category.Id;
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string SideEffects => Category.SideEffects;
    public RiskLevel Risk => Category.Risk;
    public string RiskText => "Risco " + Category.Risk.Pt().ToLowerInvariant();
    public bool RequiresAdmin => Category.RequiresAdmin;
    public bool Irreversible => Category.Irreversible;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText), nameof(CanSelect), nameof(FilesText))] private bool _isAnalyzed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanSelect))] private bool _available = true;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SizeText), nameof(CanSelect))] private long _bytes;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FilesText), nameof(CanSelect))] private int _files;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private string _status = "Ainda não analisado";
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private bool _cleaned;

    public string SizeText => IsAnalyzed && Available ? Fmt.Bytes(Bytes) : "—";
    public string FilesText => IsAnalyzed && Available ? (Category.Kind == CleanupKind.RecycleBin ? $"{Files} item(ns)" : $"{Files} arquivo(s)") : "";
    /// <summary>Status exibido sob o tamanho; omite "Análise: X" (o tamanho já aparece logo acima).</summary>
    public string? StatusText => Status.StartsWith("Análise:", StringComparison.Ordinal) ? null : Status;
    /// <summary>Selo de risco só quando há algo a ponderar (risco acima do mínimo).</summary>
    public bool ShowRisk => Category.Risk != RiskLevel.Minimal;
    /// <summary>Só pode ser marcada se existe e (ainda não analisada ou há algo a remover).</summary>
    public bool CanSelect => Available && (!IsAnalyzed || Files > 0 || Bytes > 0);

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);
    public event EventHandler? SelectionChanged;

    public void Apply(CleanupCategoryResult r)
    {
        Available = r.Available;
        Bytes = r.Bytes;
        Files = r.Files;
        IsAnalyzed = true;
        Notes = r.Notes.Count > 0 ? string.Join(" · ", r.Notes) : null;
        if (!r.Available) { Status = "Não encontrado"; IsSelected = false; return; }
        if (r.DryRun)
        {
            Cleaned = false;
            Status = r.Files == 0 ? "Nada a limpar" : $"Análise: {Fmt.Bytes(r.Bytes)}";
            if (r.Files == 0 && r.Bytes == 0) IsSelected = false;
        }
        else
        {
            Cleaned = true;
            Status = $"Liberado {Fmt.Bytes(r.Bytes)}" + (r.Skipped > 0 ? $" · {r.Skipped} em uso mantido(s)" : "");
            IsSelected = false;
            // Após a limpeza, o "restante" é desconhecido até nova análise.
            Bytes = 0; Files = 0; IsAnalyzed = false;
        }
    }
}

public sealed record CleanupHistoryRow(string When, string Detail, bool Success);
