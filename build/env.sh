# Uso: source build/env.sh  (Git Bash) — aponta para o SDK .NET instalado no perfil do usuário
export DOTNET_ROOT="$LOCALAPPDATA/Microsoft/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
