# Coleção do Bruno

97 requisições, geradas a partir do OpenAPI da própria API. Abre no [Bruno](https://usebruno.com)
ou roda pelo terminal.

```powershell
# 1. API no ar, em Development (o Swagger só existe lá).
dotnet run --project src/Nexora.Api

# 2. Gerar (ou regerar) a coleção.
node ferramentas/bruno/gerar.mjs

# 3. Abrir: no Bruno, "Open Collection" -> ferramentas/bruno/nexora
```

No Bruno, escolha o ambiente **local** no canto superior direito, preencha a variável secreta
`senha`, e rode **`POST /auth/login`**. Ele guarda o token no ambiente — todas as outras
requisições passam a funcionar sozinhas.

## Pelo terminal

```powershell
cd ferramentas/bruno/nexora
npx @usebruno/cli run Auth --env local --env-var senha=SUA_SENHA
npx @usebruno/cli run Painel --env local --env-var senha=SUA_SENHA
```

⚠️ `bru run -r` na raiz roda **tudo**, incluindo `DELETE`, `POST /dev/semear` (que apaga e recria
o tenant) e `POST /demonstracao/semear`. Rode por pasta, ou saiba o que está chamando.

## Como ela é montada

| decisão | por quê |
|---|---|
| **gerada, não escrita à mão** | São 97 operações. Coleção manual envelhece no primeiro endpoint novo, e o jeito de descobrir é alguém tentar usar uma rota que mudou de forma há três semanas |
| **bearer na coleção**, requisição com `auth: inherit` | 87 requisições protegidas dividem um `{{token}}` só. Declarar por requisição daria 87 cópias para desatualizar |
| rota pública com **`auth: none`** | Vem do documento OpenAPI, não de lista à mão — ver a seção abaixo |
| **login guarda o token** | Único script da coleção. Sem ele, cada expiração vira copiar-e-colar de JWT |
| **id vira variável por pasta** | `{id}` é `{{contatoId}}` em Contatos e `{{canalId}}` em Canais. O fluxo "lista, copia um id, usa nas outras" funciona |
| **filtro opcional entra desligado** (`~`) | Coleção que manda todo filtro preenchido devolve lista vazia no primeiro clique, e quem abriu conclui que a API está quebrada |
| **corpo de exemplo a partir do schema** | Requisição com corpo vazio obriga a ler o Swagger em paralelo — que é o trabalho que a coleção deveria poupar |
| **`senha` e `token` são `vars:secret`** | O Bruno guarda fora do `.bru`. É o que permite versionar a coleção sem versionar credencial |
| **`X-Chave-Admin` entra por lista à mão** | O documento OpenAPI não o declara — ver abaixo |

### O cabeçalho que o documento não declara

`POST /cadastro/empresa` e `POST /demonstracao/semear` exigem o header `X-Chave-Admin`. Ele é lido
de `Request.Headers` **dentro da ação**, não é parâmetro declarado — então o Swashbuckle não o põe
no documento, e o gerador, que só lê o documento, não teria como saber que ele existe.

Sem isso as duas requisições nasciam sem a chave e voltavam `401 {"erro":"Não autorizado."}` — a
**mesma** resposta de chave errada, deliberadamente sem pista ([CadastroController.cs:52]). Quem
abrisse a coleção iria caçar a chave certa quando o que faltava era o header.

O valor vem de user-secrets, `Cadastro:ChaveAdministracao`, e entra no Bruno como a variável
secreta `chaveAdministracao`. **Vazia = as duas rotas ficam desligadas**, o que é o padrão.

Isto é remendo: um `CABECALHO_EXTRA` escrito à mão no `gerar.mjs`, que apodrece calado se a rota
mudar de lugar. Por isso o gerador **falha** (`exit 1`) se alguma rota do mapa sumir do documento.
O conserto de raiz é a ação **declarar** a exigência num atributo lido tanto pelo pipeline quanto
por um filtro de Swagger — do jeito que `[Authorize]` já é —, e aí o mapa deixa de existir.

### A correção que este trabalho exigiu na API

O documento OpenAPI marcava **toda** operação como exigindo Bearer — incluindo
`POST /api/auth/login`, que existe exatamente para quem ainda não tem token. Era
`AddSecurityRequirement` global, sem exceção para `[AllowAnonymous]`.

Na tela do Swagger isso era só um cadeado errado. Aqui o custo é real: a coleção é **gerada a
partir do documento**, e nasceria mandando `Authorization` num endpoint que não tem token ainda.

A exigência passou a ser aplicada **por operação**, lendo os mesmos atributos que o pipeline lê em
runtime — ver `src/Nexora.Api/Seguranca/FiltroSegurancaSwagger.cs`. Resultado: **10 rotas públicas,
87 protegidas**, e o Swagger UI também parou de mentir.

Detalhe que custou uma tentativa: **não dá para manter a exigência global e zerar (`Security = []`)
nas anônimas.** O escritor do `Microsoft.OpenApi` omite coleção vazia, então `security: []` — que
no OpenAPI significa "esta operação dispensa a exigência global" — some do JSON e vira
indistinguível de "não declarei nada". A global voltava a valer.

## Regerar

A coleção é **artefato**. Mexeu em endpoint, rode `node ferramentas/bruno/gerar.mjs` de novo — o
gerador apaga e recria a pasta, então rota que deixou de existir some junto. Sobra de execução
anterior é a forma mais silenciosa de uma coleção mentir.

O que **não** é regerado: nada. Se você editar um `.bru` à mão, a próxima geração descarta. Ajuste
que precise sobreviver vai no `gerar.mjs`.

## Verificado

`Auth`, `Painel` e os dois `GET` de `Contatos` rodaram pelo `@usebruno/cli` contra a API local:
4 requisições, 4 OK, 1 teste passando. Isso cobre o que não dava para conferir só lendo — que o
`.bru` parseia, que o bearer é herdado da coleção, e que `params:path` e `params:query` resolvem.

`POST /cadastro/empresa` rodou duas vezes, numa cópia da coleção com `emailDono` apontando para um
e-mail que **já existe** — assim a requisição bate na checagem de unicidade *depois* do portão da
chave, e nada é criado:

| chave enviada | resposta | o que prova |
|---|---|---|
| errada | `401 Unauthorized` | a comparação recusa |
| certa | `400 Bad Request` (e-mail duplicado) | **passou do 401** — o header chegou e foi aceito |

Confirmado no banco que nenhuma empresa foi criada.

As outras 92 requisições **não** foram executadas: entre elas há `DELETE` e semeadura que apagam
dados. O que se sabe delas é que foram geradas pelo mesmo caminho de código das que rodaram.
