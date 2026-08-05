# Bloco 1 — Scaffold da solução + andaime

Estado: **fechado**. Os 6 critérios de pronto passam (evidências no fim).

A API autentica um usuário, isola o tenant e trata erro de forma coerente. Nada além disso.

---

## 1. O que foi portado, e de onde

Origem sempre `recupera/` (lido, nunca alterado).

| Destino no Nexora | Origem no Recupera | O que mudou |
|---|---|---|
| `src/Nexora.Core/IContextoEmpresa.cs` | `src/Recupera.Core/IContextoEmpresa.cs` | Literal. Comentário da armadilha ampliado: agora diz explicitamente que `IgnoreQueryFilters` sozinho não basta — precisa vir com filtro por `empresaId`. |
| `src/Nexora.Api/ContextoEmpresaHttp.cs` | `src/Recupera.Api/ContextoEmpresaHttp.cs` | Literal, com os comentários do `MapInboundClaims` preservados. A menção a `acordos.criado_por` virou "a coluna de autoria". |
| `src/Nexora.Core/Seguranca/HashSenha.cs` | `src/Recupera.Core/Seguranca/HashSenha.cs` | Literal. |
| `src/Nexora.Core/Seguranca/PoliticaLogin.cs` | `src/Recupera.Core/Seguranca/PoliticaLogin.cs` | Literal, só a frase-semente do `HashDummy` traduzida. |
| `src/Nexora.Api/Seguranca/GeradorToken.cs` | `src/Recupera.Api/Seguranca/GeradorToken.cs` | **Removidos** `ClaimSuper`, `ClaimEscopo`, `ClaimAdminId` e o método `GerarSuper`. Sobrou um método só. |
| `src/Nexora.Api/Program.cs` (wiring JWT) | `src/Recupera.Api/Program.cs:61-99` | `OnMessageReceived` do `/hub` mantido íntegro; `AddSignalR`/`MapHub` **não** — ver §4. Policy `SuperAdmin` removida. |
| `src/Nexora.Core/Servicos/RegraDeNegocioException.cs` | idem no Recupera | Literal. |
| `src/Nexora.Api/FiltroRegraDeNegocio.cs` | `src/Recupera.Api/FiltroRegraDeNegocio.cs` | `EnvioWhatsAppException` → `IntegracaoWhatsAppException` (novo tipo, §2). Exemplos nos comentários trocados de "acordo vigente" para "conversa aberta". |
| `src/Nexora.Api/Seguranca/RateLimitingConfig.cs` | `src/Recupera.Api/Seguranca/RateLimitingConfig.cs` | Só as políticas **geral** e **login**. As de senha, convite e webhook ficaram de fora — ver §4. |
| `src/Nexora.Api/Program.cs` (CORS + ForwardedHeaders) | `src/Recupera.Api/Program.cs:120-143` | Literal. |
| `src/Nexora.Api/Program.cs` (Options por seção) | `src/Recupera.Api/Program.cs:22-34,105-118` | Adaptado: `ConnectionStrings:Nexora`, `Jwt`, `RateLimit`, `Cors`. Validação de boot da chave JWT mantida. |
| `src/Nexora.Infra/Servicos/ServicoAutenticacao.cs` | `src/Recupera.Infra/Servicos/ServicoAutenticacao.cs` | Lógica idêntica, com o bloco `===== A ARMADILHA =====` preservado e ampliado. `FalhasConsecutivas`→`FalhasLogin`, `UltimoLoginEm`→`UltimoAcessoEm`, `StatusUsuario` de 3 estados. Recebe `TimeProvider` (o Recupera usa `DateTime.UtcNow` direto), para o teste de bloqueio não precisar esperar 15 minutos reais. |
| `tests/Nexora.Tests/Integracao/BancoTeste.cs` | `tests/Recupera.Tests/Integracao/BancoTeste.cs` | Reescrito mantendo a ideia (Postgres real + transação sempre revertida). Ver §2 — ganhou auto-provisionamento. |
| `global.json` | `global.json` | Literal. |

### Copiado só em parte — o que ficou de fora

- **`GeradorToken`**: todo o caminho de plataforma (`GerarSuper` e os três claims de backoffice).
- **`RateLimitingConfig`**: `PolSenha`, `PolConvite`, `PolWebhook`. Política de rate limit sem endpoint para proteger é código morto que ninguém revisa; entram junto com as rotas.
- **`Program.cs`**: `AddSignalR`, `MapHub`, os três `AddHostedService` (motor/expurgo/métricas), o bootstrap do super-admin, o registro de storage S3 e o de Evolution.
- **`FiltroRegraDeNegocio`**: nada removido, mas o `case` de WhatsApp aponta para uma exceção que ainda não tem quem a lance (§4).

---

## 2. O que foi escrito do zero

| Arquivo | O que é |
|---|---|
| `src/Nexora.Core/Entidades/Empresa.cs`, `Usuario.cs`, `Enums.cs` | As duas únicas entidades do bloco, conforme a especificação de schema. |
| `src/Nexora.Core/Entidades/IEntidadeAuditada.cs` | Marcador que o interceptor usa para achar quem carimbar. |
| `src/Nexora.Core/Servicos/IntegracaoWhatsAppException.cs` | Substitui `EnvioWhatsAppException`. Declarada já agora porque quem traduz exceção para HTTP é o filtro global, e ele precisa conhecer o tipo. |
| `src/Nexora.Core/Servicos/IServicoAutenticacao.cs` | Contrato + record `UsuarioAutenticado`. |
| `src/Nexora.Infra/Persistencia/NexoraDbContext.cs` | Mapeamento explícito snake_case, query filter global por `empresa_id`, checks e índices. |
| `src/Nexora.Infra/Persistencia/InterceptorAuditoria.cs` | Preenche `criado_em`/`atualizado_em` no `SaveChanges`. |
| `src/Nexora.Infra/Persistencia/FabricaDbContextDesignTime.cs` | Deixa `dotnet ef` funcionar num clone limpo, sem user-secrets. |
| `src/Nexora.Infra/Persistencia/Migrations/*_Inicial.cs` | A migration inicial (+ SQL para o que o EF não modela). |
| `src/Nexora.Infra/ServicosInfra.cs` | Extensão de DI: data source com enums nativos, DbContext, interceptor, serviços. |
| `src/Nexora.Api/Controllers/AuthController.cs` | `POST /api/auth/login`. |
| `Dockerfile`, `.dockerignore`, `docker-compose.yml`, `.env.example` | Build da API e o Postgres de dev. |
| `frontend/nexora-painel/` | Esqueleto Angular 20 zoneless, `pt-BR`, environments, estrutura de pastas. |
| `tests/Nexora.Tests/**` | 21 testes. |

Melhorias deliberadas sobre o original:

- **`BancoTeste` se provisiona sozinho.** Cria o banco de teste se não existir e roda `Database.Migrate()`. No Recupera é preciso criar o banco e aplicar o `.sql` à mão antes de rodar os testes — passo que não está escrito em lugar nenhum e que quebra para quem clona o repo.
- **Tokens de convite e de reset indexados.** O Recupera busca `TokenConvite == token` sem índice: varredura completa da tabela a cada aceite. Aqui são dois índices únicos parciais.
- **`senha_hash` anulável + check `ck_usuarios_senha`.** Só `status = 'convidado'` pode ficar sem hash. Sustenta o fluxo de convite sem permitir usuário ativo sem senha.

---

## 3. Decisões que tomei por conta própria

**1. A especificação de schema tem outro nome.** O prompt aponta para `docs/schema_nexora_fase1.sql`; o arquivo existente é `docs/SCHEMA-NEXORA.sql`. É o mesmo artefato (mesmo conteúdo, gerado antes neste projeto). Segui com ele em vez de parar. Se o nome importa, é um `git mv`.

**2. Interceptor **e** trigger, não um ou outro.** A proibição 3 pede interceptor, e ele está lá. Mas interceptor só vê o que passa pelo EF — e o bloco 4 vai escrever a outbox com `INSERT ... ON CONFLICT` em SQL cru, que não dispara `SaveChanges`. A migration instala também `fn_atualizado_em()` como trigger. Os dois se sobrepõem de propósito: o interceptor cobre `criado_em` no INSERT, o trigger cobre qualquer UPDATE venha de onde vier.

**3. FK `ON DELETE RESTRICT`, não `CASCADE`.** O EF gera `CASCADE` por padrão para relação obrigatória; a especificação diz `REFERENCES empresas(id)` puro. Apagar uma empresa não pode levar os usuários junto por acidente — não há delete físico no desenho.

**4. `dotnet-ef` fixado como ferramenta LOCAL (8.0.11).** A ferramenta global instalada é a 10.0.5, e contra um projeto net8 ela gerava migration mas dava **timeout** no `database update`. O manifesto em `.config/dotnet-tools.json` prende a versão junto do repositório. Use `dotnet dotnet-ef ...`, não `dotnet ef ...`.

**5. Rate limit só onde há rota.** Ver §1.

**6. Porta de dev 5123.** É o que o `launchSettings.json` gerado traz, e é o valor que está no `environment.development.ts`. Não inventei 5280/7074 — foi exatamente a divergência entre comentário e código que apontei no `api-base.ts` do Recupera.

**7. `docker-compose` publica o Postgres na porta 5433 do host.** Quem já tem Postgres local na 5432 (caso comum) não precisa desligá-lo.

**8. Sem `AddSignalR`/`MapHub`.** O prompt manda portar o *wiring* JWT do SignalR, e ele está lá (o `OnMessageReceived` que resgata o token da query string). Registrar o hub sem hub é peça solta; entra no bloco da caixa de entrada.

---

## 4. Três armadilhas que custaram depuração — registradas para os próximos blocos

**`HasPostgresEnum<T>("nome")` passa a string como SCHEMA, não como nome do tipo.**
A assinatura é `HasPostgresEnum<T>(schema, name, nameTranslator)`. Com um argumento posicional, a migration emite a anotação `Npgsql:Enum:papel_usuario_enum.papel_usuario` — schema `papel_usuario_enum`, tipo `papel_usuario` — e o `database update` morre com `42704: tipo "papel_usuario_enum" não existe`. O correto é `HasPostgresEnum<T>(name: "...")`.

> **Divergência com o Recupera, resolvida a favor do código.** As 14 chamadas em `RecuperaDbContext.cs:54-67` usam o argumento posicional, ou seja, têm o mesmo defeito. Lá o problema fica latente: a migration `20260804023909_Baseline` **não** emite DDL gerado a partir do modelo — ela lê um `Baseline.sql` embutido como recurso e o executa via `migrationBuilder.Sql`. Ou seja, a anotação `Npgsql:Enum` existe no modelo e nunca chega a virar `CREATE TYPE`. Não é bug em produção no Recupera; passa a ser no dia em que alguém gerar uma migration comum (não-baseline) a partir daquele contexto.
>
> Conferido no HEAD `5e22f3e`. O repositório do Recupera foi commitado durante esta sessão — se o baseline mudar de estratégia, esta observação precisa ser revisitada.

**`MapEnum` no data source exige que o tipo já exista no banco.**
O Npgsql resolve o OID de cada enum mapeado ao abrir a conexão. Num banco vazio o tipo ainda não existe — e quem o cria é justamente a migration que se quer aplicar. Resultado: `dotnet ef database update` falha antes de rodar qualquer DDL. Por isso `FabricaDbContextDesignTime` e o `AplicarMigrations` do `BancoTeste` usam `UseNpgsql(connectionString)` puro, **sem** `MapEnum`; o mapeamento existe só no runtime (`ServicosInfra`) e no data source que o `BancoTeste` monta **depois** de migrar.

**Sem `MapEnum` em tempo de design, o EF erra o tipo da coluna e o default.**
Consequência da anterior: sem o mapeamento, o gerador emite `papel integer` em vez de `papel papel_usuario_enum`, e renderiza `HasDefaultValue(StatusUsuario.Ativo)` como `DEFAULT 0`. Solução adotada: `HasColumnType("papel_usuario_enum")` explícito no modelo, e o default do enum aplicado por `ALTER TABLE ... SET DEFAULT 'ativo'` dentro da migration.

---

## 5. O que ficou pendente

**Não há como criar o primeiro usuário pela aplicação.** O bloco entrega autenticação, não cadastro — `ServicoEmpresa`, convite e aceite não estavam no escopo. Hoje o primeiro usuário entra por SQL (o passo está no §7). Um endpoint de cadastro, ou um bootstrap por configuração, precisa de dono num bloco seguinte.

**O índice `uq_usuarios_email` não aparece no `ModelSnapshot`.** É índice por expressão (`lower(email)`), que o EF Core não modela; ele vive como `migrationBuilder.Sql` dentro da migration. Consequência prática: uma migration futura não vai recriá-lo nem detectar se alguém o dropar à mão. Está comentado no arquivo.

**Rate limit é em memória.** Vale para instância única. Duas instâncias e o limite passa a valer por processo. Já documentado no `RateLimitingConfig`; virará problema real quando houver escala horizontal.

**Sem CI.** O prompt não pediu, e é o item nº 2 da lista de observações do inventário sobre o Recupera. Vale abrir no próximo bloco, porque o valor de `dotnet test` verde cai muito se ninguém o roda automaticamente.

**Sem envio de e-mail.** Consequência conhecida do inventário: convite e reset de senha vão precisar disso, e não existe nada. Bloco 2.

**Teste de login é no nível de serviço, não HTTP.** Os testes exercitam `ServicoAutenticacao` + `GeradorToken` e conferem o claim `empresa_id` dentro do JWT. O endpoint em si foi validado à mão nesta sessão (login 200 com token, credencial inválida 401, sexta tentativa 429 com `Retry-After`), mas não há `WebApplicationFactory` no `dotnet test`. Se o HTTP tiver que ser coberto por teste automatizado, é trabalho de um bloco futuro.

---

## 6. Critérios de pronto — evidências

| # | Critério | Resultado |
|---|---|---|
| 1 | `dotnet build` limpo | **0 Aviso(s), 0 Erro(s)** |
| 2 | Migration equivalente à especificação | Aplicada em banco limpo e conferida objeto a objeto — ver abaixo |
| 3 | `dotnet test` verde | **21 aprovados, 0 falhas** |
| 4 | `GET /health` responde 200 | `Healthy` / `HTTP 200` |
| 5 | `ng build` limpo | bundle 204,55 kB (55,70 kB transferidos) |
| 6 | `docker build` conclui | `nexora-api:bloco1`, 335 MB |

Conferência do DDL aplicado (`\d` do Postgres 17, comparado com `docs/SCHEMA-NEXORA.sql`):

- Enums nativos: `papel_usuario_enum = dono,gestor,vendedor`; `status_usuario_enum = ativo,convidado,inativo` — nomes, valores e ordem conferem.
- `empresas.id` e `usuarios.id`: `identity = YES`, `generation = ALWAYS`.
- `usuarios.papel : papel_usuario_enum`, `usuarios.status : status_usuario_enum DEFAULT 'ativo'::status_usuario_enum`.
- Índices: `uq_usuarios_email` em `lower(email)`; `uq_usuarios_token_convite` e `uq_usuarios_token_reset` parciais (`WHERE ... IS NOT NULL`); `ix_usuarios_empresa (empresa_id, status)` e `uq_usuarios_id_empresa (id, empresa_id)` — `empresa_id` presente nos compostos.
- Checks: `ck_empresas_janela`, `ck_empresas_hora_faixa`, `ck_empresas_dias`, `ck_usuarios_senha`.
- Triggers: `tg_empresas_atualizado`, `tg_usuarios_atualizado`.
- FK: `FOREIGN KEY (empresa_id) REFERENCES empresas(id) ON DELETE RESTRICT`.

Os 21 testes cobrem, entre outros, os cinco mínimos exigidos: `HashSenha` (gera, valida, e rejeita 7 formas de hash adulterado, incluindo um bit virado no digest); `PoliticaLogin` (nove falhas não bloqueiam, a décima bloqueia, senha certa não passa enquanto bloqueada, e volta a passar após a janela — com relógio falso); JWT com claim `empresa_id`; tempos comparáveis entre e-mail inexistente e senha errada; e **isolamento cross-tenant contra Postgres real**, incluindo o caso em que `EmpresaId = 0` devolve vazio em silêncio.

---

## 7. Como rodar do zero

Pré-requisitos: .NET SDK 8, Node 20+, Docker, e um Postgres (o do compose ou um local).

```bash
git clone <repo> nexora && cd nexora

# 1. Postgres de desenvolvimento (ou use um já instalado)
cp .env.example .env          # preencha POSTGRES_PASSWORD
docker compose up -d

# 2. Ferramenta de migration, na versão fixada pelo repositório
dotnet tool restore

# 3. Segredos locais (nunca no appsettings versionado)
dotnet user-secrets set "ConnectionStrings:Nexora" \
  "Host=localhost;Port=5433;Database=nexora;Username=nexora;Password=<a do .env>" \
  --project src/Nexora.Api
dotnet user-secrets set "Jwt:Chave" "$(openssl rand -hex 32)" --project src/Nexora.Api

# 4. Schema
dotnet dotnet-ef database update --project src/Nexora.Infra --startup-project src/Nexora.Api

# 5. API  ->  http://localhost:5123  (Swagger em /swagger)
dotnet run --project src/Nexora.Api

# 6. Painel  ->  http://localhost:4200
cd frontend/nexora-painel && npm install && npm start
```

Testes:

```bash
dotnet test
```

O `BancoTeste` cria o banco `nexora_teste` sozinho e aplica as migrations. Ele espera
`Host=localhost;Port=5432;Database=nexora_teste;Username=postgres;Password=admin`; para
apontar noutro lugar, defina `NEXORA_TESTE_CONN`.

Imagem da API:

```bash
docker build -t nexora-api .
```

### Primeiro usuário

Não há endpoint de cadastro ainda (§5). Para conseguir logar, insira à mão — o hash tem que
estar no formato `pbkdf2$100000$<salt-b64>$<hash-b64>` (PBKDF2-SHA256, 100k iterações, salt de
16 bytes, digest de 32):

```sql
INSERT INTO empresas (nome) VALUES ('Minha Empresa');
INSERT INTO usuarios (empresa_id, nome, email, senha_hash, papel, status)
VALUES (1, 'Fulana', 'fulana@empresa.com', '<hash>', 'dono', 'ativo');
```

Um hash de teste pode ser gerado com Node:

```bash
node -e "const c=require('crypto');const s=c.randomBytes(16);
console.log('pbkdf2\$100000\$'+s.toString('base64')+'\$'+
  c.pbkdf2Sync(Buffer.from('SUA-SENHA','utf8'),s,100000,32,'sha256').toString('base64'))"
```
