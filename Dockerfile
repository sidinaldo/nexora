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

# ===================== A PASTA DE MÍDIA PRECISA DE DONO (INF-1) =====================
# Criada AQUI, com dono, e não deixada a cargo do volume — a ordem importa.
#
# Quando um volume nomeado VAZIO é montado sobre um diretório que já existe na
# imagem, o Docker copia o conteúdo E O DONO daquele diretório para o volume. Se o
# diretório não existisse, o Docker o criaria como ROOT, e a aplicação (que roda
# como `app`) receberia "Access denied" na primeira foto que o cliente mandasse.
#
# O modo de falha é silencioso do lado errado: a mensagem entra, o download da
# mídia falha, e `mensagens.erro` guarda um erro de permissão que ninguém lê. Um
# `chown` de uma linha evita isso.
#
# O caminho casa com `Midia__Raiz=/app/midia` do docker-compose.prod.yml.
RUN mkdir -p /app/midia && chown app:app /app/midia

# Usuário sem privilégio, já presente na imagem oficial.
USER app

# 8080 é a porta padrão do Kestrel nas imagens .NET 8 sem root (não 80).
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

# Sem HEALTHCHECK aqui de propósito: a imagem runtime não traz curl nem wget, e instalar
# um só para isso engorda o container. Quem observa é o orquestrador, batendo em /health.
ENTRYPOINT ["dotnet", "Nexora.Api.dll"]
