namespace Rbm.Core;

/// <summary>Resultado de uma operação, sem exceções propagadas para a UI.</summary>
public sealed record OperationResult(bool Success, string Message, string? ErrorCode = null)
{
    public static OperationResult Ok(string message = "OK") => new(true, message);
    public static OperationResult Fail(string message, string? code = null) => new(false, message, code);
}

public sealed record OperationResult<T>(bool Success, T? Value, string Message, string? ErrorCode = null)
{
    public static OperationResult<T> Ok(T value, string message = "OK") => new(true, value, message);
    public static OperationResult<T> Fail(string message, string? code = null) => new(false, default, message, code);
}

/// <summary>Valor que pode estar indisponível (sensor ausente, sem permissão etc.).</summary>
public readonly record struct Reading<T>(bool Available, T Value, string? Source = null) where T : struct
{
    public static Reading<T> None => new(false, default, null);
    public static Reading<T> Of(T value, string? source = null) => new(true, value, source);
    public override string ToString() => Available ? Value.ToString() ?? "" : "—";
}

/// <summary>Texto bilíngue (pt-BR padrão, inglês opcional).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(LocalizedTextConverter))]
public sealed record LocalizedText(string Pt, string? En = null)
{
    public string Get(string lang) => lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(En) ? En! : Pt;
    public override string ToString() => Pt;
    public static implicit operator LocalizedText(string pt) => new(pt);
}
