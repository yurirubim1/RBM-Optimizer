using System.ComponentModel;

namespace Rbm.Processes;

/// <summary>Códigos de erro estáveis devolvidos em <see cref="Rbm.Core.OperationResult.ErrorCode"/>.</summary>
public static class ProcessErrorCodes
{
    public const string AccessDenied = "AccessDenied";
    public const string ElevationRequired = "ElevationRequired";
    public const string NotFound = "NotFound";
    public const string InvalidArgument = "InvalidArgument";
    public const string Protected = "Protected";
    public const string ConfirmationRequired = "ConfirmationRequired";
    public const string PidReused = "PidReused";
    public const string Timeout = "Timeout";
    public const string DependentsRunning = "DependentsRunning";
    public const string Unavailable = "Unavailable";
    public const string Failed = "Failed";
}

/// <summary>Tradução de erros Win32/HRESULT para mensagens pt-BR acionáveis.</summary>
public static class Win32Messages
{
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_DEPENDENT_SERVICES_RUNNING = 1051;
    public const int ERROR_INVALID_SERVICE_CONTROL = 1052;
    public const int ERROR_SERVICE_REQUEST_TIMEOUT = 1053;
    public const int ERROR_SERVICE_ALREADY_RUNNING = 1056;
    public const int ERROR_SERVICE_DISABLED = 1058;
    public const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    public const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061;
    public const int ERROR_SERVICE_NOT_ACTIVE = 1062;
    public const int ERROR_SERVICE_DEPENDENCY_FAIL = 1068;
    public const int ERROR_SERVICE_MARKED_FOR_DELETE = 1072;

    /// <summary>Mensagem pt-BR para um erro Win32. <paramref name="elevated"/> evita sugerir o agente quando já estamos nele.</summary>
    public static string Describe(int error, bool elevated = false) => error switch
    {
        ERROR_ACCESS_DENIED => elevated
            ? "Acesso negado mesmo com privilégios administrativos (processo/serviço protegido pelo sistema)."
            : "Acesso negado. O item pertence a outro usuário, é elevado ou protegido. Tente pelo agente RBM (requer elevação).",
        ERROR_FILE_NOT_FOUND => "Item não encontrado.",
        ERROR_INVALID_HANDLE => "Handle inválido (o processo pode ter terminado).",
        ERROR_INVALID_PARAMETER => "Parâmetro inválido (o processo pode ter terminado).",
        ERROR_DEPENDENT_SERVICES_RUNNING => "Há serviços dependentes em execução; pare-os antes.",
        ERROR_INVALID_SERVICE_CONTROL or ERROR_SERVICE_CANNOT_ACCEPT_CTRL => "O serviço não aceita este comando no estado atual.",
        ERROR_SERVICE_REQUEST_TIMEOUT => "O serviço não respondeu a tempo.",
        ERROR_SERVICE_ALREADY_RUNNING => "O serviço já está em execução.",
        ERROR_SERVICE_DISABLED => "O serviço está desabilitado; altere o tipo de inicialização antes de iniciá-lo.",
        ERROR_SERVICE_DOES_NOT_EXIST => "Serviço não encontrado.",
        ERROR_SERVICE_NOT_ACTIVE => "O serviço não está em execução.",
        ERROR_SERVICE_DEPENDENCY_FAIL => "Um serviço do qual este depende não pôde ser iniciado.",
        ERROR_SERVICE_MARKED_FOR_DELETE => "O serviço está marcado para exclusão.",
        _ => $"Erro do Windows {error}: {new Win32Exception(error).Message}",
    };

    public static string CodeFor(int error) => error switch
    {
        ERROR_ACCESS_DENIED => ProcessErrorCodes.AccessDenied,
        ERROR_FILE_NOT_FOUND or ERROR_SERVICE_DOES_NOT_EXIST => ProcessErrorCodes.NotFound,
        ERROR_INVALID_PARAMETER => ProcessErrorCodes.InvalidArgument,
        ERROR_DEPENDENT_SERVICES_RUNNING => ProcessErrorCodes.DependentsRunning,
        ERROR_SERVICE_REQUEST_TIMEOUT => ProcessErrorCodes.Timeout,
        _ => $"Win32:{error}",
    };

    public static Rbm.Core.OperationResult Fail(string prefix, int error, bool elevated = false) =>
        Rbm.Core.OperationResult.Fail($"{prefix}: {Describe(error, elevated)}", CodeFor(error));
}
