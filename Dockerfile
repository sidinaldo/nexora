# =============================================================================
#  Nexora — API (.NET 8), build multi-stage.
#  Contexto de build = raiz do repositório:  docker build -t nexora-api .
# =============================================================================

# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Os .csproj primeiro, sozinhos: enquanto as dependências não mudarem, a camada de
# restore fica em cache e uma alteração de código não baixa pacote de novo.
# O global.json vem junto porque ele decide qual SDK o restore usa.
COPY global.json ./
COPY src/Nexora.Core/Nexora.Core.csproj  src/Nexora.Core/
COPY src/Nexora.Infra/Nexora.Infra.csproj src/Nexora.Infra/
COPY src/Nexora.Api/Nexora.Api.csproj    src/Nexora.Api/
RUN dotnet restore src/Nexora.Api/Nexora.Api.csproj

COPY src/ src/
RUN dotnet publish src/Nexora.Api/Nexora.Api.csproj \
      -c Release -o /app/publish --no-restore

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Imagem só com o runtime (não o SDK): menor e sem compilador em produção.
COPY --from=build /app/publish .

# Usuário sem privilégio, já presente na imagem oficial.
USER app

# 8080 é a porta padrão do Kestrel nas imagens .NET 8 sem root (não 80).
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Sem HEALTHCHECK aqui de propósito: a imagem runtime não traz curl nem wget, e instalar
# um só para isso engorda o container. Quem observa é o orquestrador, batendo em /health.
ENTRYPOINT ["dotnet", "Nexora.Api.dll"]
