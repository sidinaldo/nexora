# ARQ-2 — Multi-número: adicionar, editar e apagar conexão

O pedido foi CRUD na tela de conexão. A pergunta que decidiu o desenho foi outra:

> **"Dependendo do plano pode ter mais de uma conexão."**

Isso muda o pedido de lugar. Não é uma tela ganhando três botões — é o sistema deixando de
assumir que "a conexão da empresa" existe no singular. Este relatório separa as duas coisas: o
que virou CRUD, e o que **quebrava em silêncio** com N números.

---

## 1. O levantamento: quatro caminhos, um quebrava

Antes de escrever qualquer coisa, mapeei onde o código dizia "a conexão da empresa".

| caminho | como era | com N |
|---|---|---|
| **lead novo entra** | webhook casa o tenant por `instance_name` | ✅ já correto — a chave é a instância, não a empresa |
| **responder na caixa** | `ServicoConversas` usa `conversa.Conexao` | ✅ já correto |
| **follow-up automático** | `dados.ConexaoAsync(empresaId)` → porteiro da rodada inteira | ❌ **quebrava** |
| **banner global** | `db.Conexoes.FirstOrDefaultAsync()` | ❌ **mentia** |

Os dois primeiros já estavam certos por acidente feliz de desenho — quem decide o destino é a
conversa, e a conversa sempre teve `conexao_id`. Os dois últimos não.

### O bug do follow-up, por extenso

```csharp
var conexao = await dados.ConexaoAsync(empresa.Id, ct);      // "a" conexão
var conectada = await enviador.InstanciaConectadaAsync(conexao.Value.InstanceName, ct);
var podePostar = janelaAberta && conectada;                  // vale para TODOS os lembretes
```

Numa empresa com dois números, o de Vendas cair fazia o de Suporte **parar de mandar follow-up
também**. E o log dizia `conexão caída` — verdade sobre uma conexão, mentira sobre a rodada.

Ninguém teria descoberto por erro: as mensagens ficavam reservadas, o job seguia de pé, os
contadores fechavam. O sintoma seria o cliente reclamando semanas depois.

**A correção:** o freio virou **por instância**, uma checagem por número por rodada.

```csharp
var noAr = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
foreach (var c in conexoes)
    noAr[c.InstanceName] = await enviador.InstanciaConectadaAsync(c.InstanceName, ct);

bool PodePostarPor(string instancia) =>
    janelaAberta && noAr.TryGetValue(instancia, out var ok) && ok;
```

O destino de cada lembrete **já era** a conexão da conversa (`LembreteParaDisparar` sempre
carregou `ConexaoId`/`InstanceName`). O que faltava era o freio acompanhar. A drenagem de
pendentes idem: a linha reservada guarda o `instance_name` que a criou, e é por ele que se decide
se hoje ela sai — drenar tudo pela conexão da empresa mandaria a resposta por um telefone que o
cliente nunca contatou.

`IDadosFollowUp.ConexaoAsync` → **`ConexoesAsync`**, no plural. A empresa só é pulada quando não
tem conexão **nenhuma**.

---

## 2. O limite é dado, não schema

A trava era um índice: `uq_conexoes_empresa UNIQUE (empresa_id)`. Ele saiu.

No lugar entrou `empresas.limite_conexoes` (`smallint`, default 1, `CHECK BETWEEN 1 AND 20`).

### Por que uma coluna e não uma tabela de planos

**PLANO ainda não existe neste sistema** — não há assinatura, cobrança nem catálogo. Criar a
tabela agora seria modelar um domínio inteiro a partir de uma frase, e ela nasceria com uma linha
e nenhum dono.

O que o *código* precisa é do limite. Quem decide que o plano X dá três números é política
comercial, e por enquanto vive fora do sistema. Quando planos existirem, esta coluna passa a ser
derivada deles — e o ponto onde a regra é aplicada (`ServicoConexoes.CriarAsync`) não muda.

### Por que não ficou no schema

Um número que muda **por contrato** não pode morar num índice: subir de plano viraria migration.
Com a coluna, é `UPDATE empresas SET limite_conexoes = 3` — e há teste provando exatamente isso
(`SUBIR_O_LIMITE_LIBERA_A_SEGUNDA_SEM_MIGRATION`).

### O que se perdeu, registrado

Sem índice, **dois pedidos simultâneos podem passar os dois pela contagem** e criar uma conexão a
mais. Não há lock, de propósito: o pedido parte do dono, numa tela de configuração, um clique por
vez — e o dano é uma linha a remover, não dado corrompido. Se um dia isso importar, o lugar de
resolver é um advisory lock por empresa, **não** a volta do índice.

**Migration:** `20260806130028_MultiConexao`, aplicada em `nexora_dev`.

| índice | antes | depois |
|---|---|---|
| `uq_conexoes_empresa` | UNIQUE (empresa_id) | **removido** |
| `ix_conexoes_empresa` | — | criado (não único) |
| `uq_conexoes_empresa_nome` | — | UNIQUE (empresa_id, nome) |
| `uq_conexoes_instance` | UNIQUE global | inalterado |

---

## 3. As três invariantes do CRUD

### 3.1 `instance_name` NÃO é editável, em nenhuma circunstância

Não existe endpoint, não existe campo na tela, e o teste `RENOMEAR_MANDA_SÓ_O_NOME` prova que o
PUT não carrega o campo.

Ele é a identidade na Evolution **e** a chave pela qual o webhook acha o tenant. Renomear
orfanaria a instância e o sistema pararia de receber mensagem **em silêncio** — sem erro, sem
log, sem sintoma até alguém reclamar que o cliente não foi respondido.

O que a empresa edita é `nome`, que é rótulo de tela e mais nada.

### 3.2 O nome derivado, e por que duas passadas

`instance_name` precisa de três coisas ao mesmo tempo: único globalmente, **nunca reaproveitado**
depois de uma remoção, e legível para quem abre o painel da Evolution num suporte.

O id da conexão dá as três — é `IDENTITY ALWAYS`, nunca volta atrás, e cabe em `emp-7-3`. Só que
ele só existe **depois** do INSERT, e a coluna é `NOT NULL`. Daí:

```csharp
InstanceName = $"pendente-{Guid.NewGuid():N}";   // provisório, único por construção
await db.SaveChangesAsync(ct);                   // agora existe id
conexao.InstanceName = $"emp-{conexao.EmpresaId}-{conexao.Id}";
await db.SaveChangesAsync(ct);                   // MESMA transação
```

Se a segunda passada falhar, a primeira volta atrás e não sobra linha com nome de rascunho.

**Por que não um contador** (`emp-1`, `emp-2`, `emp-3`): apagar a terceira e criar outra
devolveria `emp-3` — e a instância antiga **pode ainda existir** do lado da Evolution. A conexão
nova adotaria a sessão dela em silêncio. É a falha mais silenciosa deste bloco inteiro, e é a que
o teste `INSTANCE_NAME_E_DERIVADO_DO_ID_E_NUNCA_REAPROVEITADO` prende.

### 3.3 Apagar: duas recusas, e as duas são invariante de dado

```
1. tem conversa OU mensagem apontando  → a FK é RESTRICT, e cascatear perderia atendimento
2. é a ÚLTIMA da empresa               → o banco não garante, e é a pior das duas
```

A segunda merece nome. Sem nenhuma conexão: o webhook não acha o tenant (ele casa por
`instance_name`), o envio não tem instância, e **nada no sistema recria uma** — a criação só
acontece no cadastro da empresa. A conta ficaria sem caminho de volta.

**`PodeRemover`/`MotivoNaoRemove` vêm do servidor**, não são deduzidos na tela: só o banco sabe se
há conversa apontando para a conexão. Sem isso a tela ofereceria um botão que às vezes devolve
erro — a pior forma de dizer "não pode".

E a mensagem é a **mesma** nos dois lados. O teste
`CONEXAO_COM_CONVERSA_NAO_PODE_SER_APAGADA` compara `erro.Message` com o
`motivoNaoRemove` que a lista mostrou. Duas cópias da regra divergiriam, e o sintoma seria um
`title` de botão dizendo uma coisa e o toast dizendo outra.

### A Evolution primeiro, e não por acaso

```csharp
await cliente.RemoverInstanciaAsync(conexao.InstanceName, ct);   // primeiro
db.Conexoes.Remove(conexao);
await db.SaveChangesAsync(ct);
```

Se a linha fosse apagada antes, uma falha aqui deixaria a instância **viva** do outro lado —
pareada, mandando webhook que ninguém reconhece — e sem o nome guardado em lugar nenhum para
alguém limpar depois. Vazamento silencioso e irrecuperável.

Na ordem contrária, o pior caso é a linha sobreviver apontando para uma instância que já não
existe. Isso o dono vê na tela, e a operação é idempotente: clicar de novo resolve. **Erro visível
e recuperável ganha de erro invisível, sempre.**

`IClienteWhatsApp.RemoverInstanciaAsync` é novo: `DELETE /instance/logout` (falha engolida — se já
estava desconectada não há o que tratar) seguido de `DELETE /instance/delete`. **404 conta como
sucesso**: instância que não existe é exatamente o estado pedido, e sem isso remover uma conexão
que nunca foi pareada — o caso mais comum — falharia.

---

## 4. Banner e onboarding

| | antes | depois |
|---|---|---|
| **banner** | status da primeira conexão | acende se **alguma pareada** caiu, e **diz qual** |
| **`trocouDeNumero`** | da primeira conexão | de **qualquer** uma |
| **onboarding passo 1** | `AnyAsync(status == conectado)` | **inalterado** — já era "ao menos uma" |

O onboarding não precisou de mudança nenhuma: ele já perguntava `Any`, e "ao menos um número
conectado" continua sendo o passo certo.

**Pareada e caída**, não só caída. `numero != null` é o que separa "caiu" de "ainda não foi
conectada" — a segunda não merece alerta vermelho no topo de todas as telas de quem acabou de
criar um número. Há teste para os dois lados.

`StatusPainel.Numero` **saiu** e `ConexoesCaidas: string[]` entrou. Um único `numero` no topo do
payload deixou de ter significado com N conexões, e mantê-lo seria deixar uma armadilha: alguém
usaria "o número da empresa" achando que existe um.

### O evento de tempo real não decide mais o banner

```ts
// antes: o evento de UMA conexão aplicava direto na flag da EMPRESA
this.realtime.conexaoMudou$.subscribe(c => this.whatsappConectado.set(c.status === 'conectado'));
```

Com N números, uma conexão voltando ao ar apagaria o alerta enquanto outra continua caída — e o
vendedor perderia o aviso justamente quando ele ainda vale. O evento agora só **pede o status de
novo**; quem sabe o agregado é o servidor.

---

## 5. A tela

`/conexao` deixou de ser um cartão e virou lista, com o mesmo desenho de linha de `/etapas`
(avatar + identidade + ações à direita).

- **criar** — nome, e abre já no pareamento; criar sem conectar não serve para nada
- **renomear** — edição em linha
- **apagar** — painel de confirmação **na página**, não `confirm()`: a alternativa (desconectar)
  precisa caber junto do aviso
- **QR / código de pareamento** — por conexão, um painel aberto por vez
- **saúde** — `enviadas / na fila / não entregues / falhas` **daquele número**

### Saúde por conexão

Era da empresa inteira, e com um número só isso dava no mesmo. Com N, o total **esconde** o que
interessa: quando um dos números está falhando, a soma continua parecendo saudável por causa dos
outros. Quem abre esta tela quer saber **qual** número está falhando.

### O polling encolheu

A tela antiga consultava o estado ao vivo **a cada 3s, sempre**. Com N números isso vira N
requisições por tick — e cada uma é um GET na Evolution, por instância.

Agora o poll existe **só enquanto o QR de uma conexão está na frente do usuário**, que é a única
situação em que 3s se justificam: é assim que a tela descobre que o pareamento deu certo. Fora
disso o status vem do banco (webhook + última consulta), como no resto do painel. Dois testes
prendem isso: um tica 10s sem QR e exige zero requisições de status; o outro prova que com o QR
o poll acontece e **para** assim que conecta.

---

## 6. Um bug antigo que apareceu junto

`ConexaoDto.Status` saía como `ToString().ToLowerInvariant()`, o que transforma `NaoCriada` em
**`"naocriada"`**. O rótulo em todo o resto do sistema é `nao_criada`: é assim no enum do Postgres
(`status_conexao_enum`), no que a Evolution devolve, e no tipo `StatusConexao` do frontend.

A divergência existia desde o bloco 3 e **nunca apareceu porque nada lia esse campo** — a tela só
olhava `conectado`. O ARQ-2 passou a mostrar o status de cada número na lista, e aí o `default` do
switch pegaria justamente a conexão recém-criada, que é a mais comum de estar nesse estado.

Corrigido em `StatusConexaoExtensoes.ParaApi()`, num lugar só, usado pela lista **e** pelo evento
de tempo real — para os dois não divergirem de novo.

---

## 7. Rotas

`/api/conexao` (singular, sem id) → **`/api/conexoes`**.

| rota | verbos | o quê |
|---|---|---|
| `/api/conexoes` | GET, POST | lista (com `limite`/`podeAdicionar`) e cria |
| `/api/conexoes/{id}` | GET, PUT, DELETE | obter, renomear (**só o nome**), apagar |
| `/api/conexoes/{id}/status` | GET | estado ao vivo na Evolution |
| `/api/conexoes/{id}/conectar` \| `/parear` | POST | QR / código |
| `/api/conexoes/{id}/desconectar` | POST | solta o celular, mantém tudo |
| `/api/conexoes/{id}/saude` | GET | contadores **daquele** número |
| `/api/conexoes/{id}/reconhecer-troca` | POST | limpa o aviso de chip trocado |

Todas `[Authorize(Roles = "dono")]`. Manter o singular e enfiar o id no corpo economizaria a
mudança no frontend e custaria a coisa que mais importa numa API: a URL dizer sobre o que ela age.

`GET /api/conexoes/{id}` devolve **404 com corpo idêntico** para "não existe" e "é de outra
empresa" — a diferença entre as respostas viraria um oráculo para descobrir quais ids existem em
outros tenants.

---

## 8. Testes

**Backend: 469 passando.** Eram **450** ao abrir o bloco — e um deles reprovava assim que a
migration entrou, porque afirmava a regra que o ARQ-2 removeu de propósito. Saldo: +19.

| arquivo | o que prende |
|---|---|
| `ConexoesDbTests` (16) | limite do plano, subir o limite sem migration, nome único (inclusive só com caixa diferente), renomear sem tocar na instância, `instance_name` derivado e não reaproveitado, última conexão, conexão com conversa, conexão com mensagem sem conversa, remoção dos dois lados, saúde por conexão, banner com N, troca de chip em qualquer uma, isolamento por tenant em **sete** métodos |
| `FollowUpDbTests` (+2) | um número caído não segura o follow-up do outro; a drenagem respeita a conexão da mensagem pendente |
| `InvariantesDbTests` | `Segunda_conexao_na_mesma_empresa_falha_na_fase_1` **invertido** para `..._e_permitida_pelo_banco`, mais dois sobre `uq_conexoes_empresa_nome` |

O teste invertido ficou no lugar em vez de ser apagado: o schema deixou de proibir **de
propósito**, e quem só ler o código novo não teria como saber que a trava existiu.

**Confirmado por mutação.** Troquei `PodePostarPor` de volta para o freio por empresa
(`noAr[conexoes[0].InstanceName]`) e os dois testes novos de follow-up reprovaram. Revertido.

**Frontend: 170 passando** (eram 161). `conexao.spec.ts` novo, com 9 casos: botão apagar
obedecendo o servidor, última conexão travada, confirmação antes do DELETE, formulário sumindo
sem vaga no plano, criar abrindo o pareamento, PUT sem `instanceName`, ausência de polling sem QR,
polling que começa com o QR e para ao conectar, e o aviso de troca de chip na linha certa.

---

## 9. O que NÃO foi feito, e por quê

- **Tabela de planos.** Ver §2. O limite é uma coluna, e quem a muda é `UPDATE` manual — não há
  fluxo de produto para trocar de plano.
- **Lock na criação.** Ver §2. A corrida existe e está registrada; o custo dela é uma linha a mais.
- **Escolher por qual número mandar uma mensagem nova.** Hoje não existe "iniciar conversa" — toda
  conversa nasce de uma mensagem de entrada, e ela já traz a instância. Quando existir, é aí que a
  escolha aparece.
- **Migrar conversas de um número para outro.** É o que tornaria uma conexão com histórico
  apagável. Operação de dado com consequência real (o cliente veria o telefone mudar), e não foi
  pedida.
- **Nada foi verificado em navegador nem com celular real.** Segue valendo o que os blocos
  anteriores registram: nenhum número foi pareado de verdade em nenhum momento deste projeto.

---

## 10. Arquivos

**Backend**

```
src/Nexora.Core/Entidades/Empresa.cs            + LimiteConexoes
src/Nexora.Core/Entidades/Enums.cs              + StatusConexaoExtensoes.ParaApi()
src/Nexora.Core/Servicos/IServicoConexoes.cs    reescrito (ConexoesDto, NovaConexao, id em tudo)
src/Nexora.Core/Servicos/IServicoPainel.cs      Numero → ConexoesCaidas
src/Nexora.Core/Whatsapp/IClienteWhatsApp.cs    + RemoverInstanciaAsync
src/Nexora.Core/FollowUp/IDadosFollowUp.cs      ConexaoAsync → ConexoesAsync
src/Nexora.Core/FollowUp/MotorFollowUp.cs       freio por instância
src/Nexora.Infra/Servicos/ServicoConexoes.cs    reescrito
src/Nexora.Infra/Servicos/ServicoPainel.cs      banner com N
src/Nexora.Infra/Persistencia/DadosFollowUp.cs  ConexoesAsync
src/Nexora.Infra/Persistencia/NexoraDbContext.cs
src/Nexora.Infra/Evolution/ClienteEvolution.cs  + RemoverInstanciaAsync
src/Nexora.Infra/Evolution/ProcessadorEventoEvolution.cs  ParaApi()
src/Nexora.Api/Controllers/ConexoesController.cs  (era ConexaoController.cs)
src/Nexora.Infra/Persistencia/Migrations/20260806130028_MultiConexao.cs
```

**Frontend**

```
src/app/paginas/conexao/{conexao.ts,conexao.html,conexao.css}  reescritos
src/app/paginas/conexao/conexao.spec.ts                        novo
src/app/nucleo/servicos/conexao.servico.ts                     rotas com id
src/app/nucleo/modelos.ts                                      Conexoes, ConexoesCaidas
src/app/layout/shell/{shell.ts,shell.html}                     banner nomeando a queda
```

**Docs**

```
docs/ARQ-2.md                 este arquivo
docs/INVENTARIO-TECNICO.md    rotas, tela, glossário, índices, limitação §resolvida
docs/SCHEMA-NEXORA.sql        seção conexoes
```

⚠️ `SCHEMA-NEXORA.sql` é um **retrato de projeto**, não o schema vivo — ele já não tinha colunas
que entraram depois (`demonstracao`, `onboarding_dispensado_em`, `uf`…). Atualizei a seção de
conexões porque ela estava contando uma regra que deixou de existir; o resto continua atrás das
migrations, e a fonte de verdade é `dotnet ef`.
