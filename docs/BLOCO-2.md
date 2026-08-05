# Bloco 2 — Domínio da fase 1, DbContext e teste de cross-tenant

Estado: **fechado**. Os 5 critérios de pronto passam (evidências no fim).

Entrega: persistência das 6 entidades novas + prova de isolamento. **Nenhum endpoint criado** —
`Controllers/` continua com o `AuthController` do bloco 1.

---

## 1. Entidades criadas

| Entidade | Tabela | Query filter | Chave alternativa (alvo de FK composta) |
|---|---|---|---|
| `Conexao` | `conexoes` | sim | `uq_conexoes_id_empresa` |
| `EtapaFunil` | `etapas_funil` | sim | `uq_etapas_id_empresa` |
| `Contato` | `contatos` | sim | `uq_contatos_id_empresa` |
| `Conversa` | `conversas` | sim | `uq_conversas_id_empresa` |
| `Mensagem` | `mensagens` | sim | — (nada referencia mensagem) |
| `Lembrete` | `lembretes` | sim | `uq_lembretes_id_empresa` |

Mais os 7 enums nativos novos (`status_conexao_enum`, `origem_lead_enum`,
`direcao_mensagem_enum`, `tipo_midia_enum`, `status_conversa_enum`, `status_lembrete_enum`,
`origem_lembrete_enum`) e os records `Pagina<T>` / `PaginaCursor<T>`, copiados literais do
Recupera.

Toda entidade de tenant tem `EmpresaId` **próprio** e query filter **próprio** — nenhuma
depende do filtro do pai em cascata, que é o padrão frágil do `TelefoneDevedor` no Recupera.

Os comentários da especificação que registram armadilhas foram para as entidades:
canonicalização de telefone (`Contato.Telefone`), o funcionamento e o preço de
`aguardando_desde` (`Conversa`), o protocolo grava→dispara→confirma (`Mensagem`), a razão de
`ordem_kanban` ser fracionário, o teto diário e a regra `<=` da data-alvo (`Lembrete`).

### Divergências em relação ao que o prompt descreve

**1. `ordem_kanban` é `numeric` sem escala, não `numeric(18,6)`.**
O prompt diz `numeric(18,6)`; o `SCHEMA-NEXORA.sql` — que o próprio prompt designa como *a
especificação* — diz `numeric` puro, com a justificativa `[C13]` no comentário: com escala 6,
inserir sempre no meio do mesmo par de cards esgota a precisão em ~19 movimentos
(2⁻¹⁹ ≈ 1e-6) e dois cards passam a colidir na mesma posição. Implementei conforme o arquivo.
Há teste provando 40 inserções consecutivas no mesmo ponto sem colisão — com `numeric(18,6)`
esse teste falha. **Se a intenção era mesmo (18,6), é uma linha para mudar e o teste avisa.**

**2. Nomes dos enums levam sufixo `_enum`.**
O prompt lista `status_conexao`, `origem_lead`, etc.; o schema define
`status_conexao_enum`, `origem_lead_enum`, etc. Segui o arquivo, que também é o que mantém a
consistência com `papel_usuario_enum` e `status_usuario_enum` do bloco 1.

**3. O arquivo de especificação chama-se `docs/SCHEMA-NEXORA.sql`,** não
`docs/schema_nexora_fase1.sql`. Mesma divergência registrada no bloco 1.

---

## 2. DDL gerado × especificação

Aplicado em banco vazio (`Inicial` + `Dominio`) e conferido objeto a objeto contra o
`SCHEMA-NEXORA.sql`.

### Bateu sem ajuste

- **9 enums nativos**, com valores e ordem idênticos (a ordem importa: `ALTER TYPE ... ADD
  VALUE BEFORE/AFTER` depois é trabalhoso).
- **6 tabelas**, todas as colunas com tipo e nulabilidade da especificação, incluindo
  `payload_raw jsonb`, `data_alvo date`, `hora_alvo time`, `valor numeric(14,2)` e
  `ordem_kanban numeric` (precisão livre — conferido em `information_schema`).
- **`GENERATED ALWAYS AS IDENTITY`** em todos os `id`.
- **Os 13 índices parciais**, com o `WHERE` exato — incluindo os quatro que carregam
  invariante de negócio (`uq_msg_wa_id`, `uq_lembrete_teto_diario`, `uq_conexoes_empresa`,
  `uq_etapas_ganho`) e o `wa_message_id <> ''` que sustenta o caso do 2xx sem `key.id`.
- **Índices com coluna descendente** (`ix_conversas_lista`, `ix_msg_timeline`,
  `ix_contatos_criado`, `ix_contatos_ganho`, `ix_conversas_responsavel`).
- **`empresa_id` como primeira coluna** de todo índice composto de consulta.
- **5 check constraints** (`ck_contatos_terminal`, `ck_conversas_nao_lidas`,
  `ck_lembretes_texto`, `ck_msg_ack`, `ck_msg_data_disparo`).
- **16 FKs compostas** `(coluna, empresa_id)`, todas `ON DELETE RESTRICT`.

### Precisou de ajuste manual na migration

Dois blocos de `migrationBuilder.Sql` — continua sendo migration (versionada, com `Down`,
aplicada por `dotnet dotnet-ef database update`); o que a regra proíbe é `.sql` solto aplicado
à mão.

1. **`DEFAULT` das 5 colunas de enum** (`conexoes.status`, `contatos.origem`,
   `conversas.status`, `mensagens.tipo_midia`, `lembretes.status`). Causa conhecida do bloco 1:
   em tempo de design o provider não carrega o mapeamento do enum, e `HasDefaultValue` sairia
   como `DEFAULT 0`, que o Postgres recusa.
2. **5 triggers de `atualizado_em`** (`conexoes`, `etapas_funil`, `contatos`, `conversas`,
   `lembretes`). `mensagens` não entra: é log append-only, sem a coluna. A função
   `fn_atualizado_em()` já vem da migration `Inicial`.

### O que o EF gerava a mais, e foi removido

O EF cria, **por convenção**, um índice para cada chave estrangeira. Com 16 FKs compostas isso
produzia **12 índices que a especificação não pede — 5 deles em `mensagens`**, a tabela de
maior taxa de escrita. O Postgres não cria índice de FK sozinho, então o DDL divergia do
schema; e a especificação vai na direção oposta (o comentário `[C10]` removeu até um índice
redundante justamente de `mensagens`).

Removidos desligando a convenção nomeada do EF, no `ConfigureConventions`:

```csharp
cfg.Conventions.Remove(typeof(ForeignKeyIndexConvention));
```

É mecanismo suportado, some do modelo **e** do snapshot (não volta numa migration futura), e o
DDL passou a bater exatamente. Índices de FK servem sobretudo para acelerar a checagem
referencial ao **apagar o pai** — aqui todas as FKs são `RESTRICT` e o desenho não tem delete
físico. **Efeito colateral a saber: relação nova não ganha índice automático.** Se uma consulta
precisar, declare explicitamente (e no schema também).

---

## 3. Testes

**58 no total** (21 vieram do bloco 1, 37 novos). Todos contra **Postgres real** — provider
in-memory não reproduz query filter no SQL, índice parcial, `ON CONFLICT` nem check
constraint, e daria verde exatamente onde importa.

### Isolamento por entidade — `IsolamentoDominioDbTests` (5)

Dois tenants completos semeados (`Semeador.TenantAsync`: empresa, dono, conexão, etapas,
contato, conversa, mensagem). Cada teste prova que existe linha do outro tenant no banco antes
de afirmar que ela não aparece.

| Teste | O que prova |
|---|---|
| `Consulta_do_tenant_A_nao_retorna_linha_do_tenant_B` | leitura das 6 entidades só devolve o próprio tenant — e o simétrico para B, para não passar por acidente se tudo voltasse vazio |
| `Buscar_por_id_do_tenant_B_devolve_null_para_o_tenant_A` | busca por id de linha alheia devolve `null` nas 6 entidades, com `ChangeTracker` limpo (senão o cache mascararia) |
| `Navegacao_a_partir_do_tenant_A_nunca_alcanca_linha_do_tenant_B` | `Include` de 3 níveis e subconsulta em projeção continuam filtrados |
| `Update_em_id_do_tenant_B_a_partir_do_tenant_A_nao_afeta_linha_nenhuma` | `ExecuteUpdate`/`ExecuteDelete` afetam **0 linhas**, a linha alheia fica intacta, e o mesmo update no próprio tenant afeta 1 |
| `Tenant_zero_devolve_vazio_em_silencio_e_o_antidoto_e_filtro_explicito` | **a rede de segurança dos próximos blocos** — ver abaixo |

O teste do tenant zero documenta o comportamento em vez de escondê-lo: com `EmpresaId = 0`
(webhook, job de fundo) as 6 consultas voltam vazias **sem erro**; com `IgnoreQueryFilters()`
mais `Where(empresaId)` voltam o esperado. Inclui o caso real do webhook — descobrir o tenant
por `instance_name`, o único lugar em que `IgnoreQueryFilters` sem `Where` é correto, porque a
chave já é global — e o contra-exemplo (`IgnoreQueryFilters` sem filtro varre os dois tenants).

### Invariantes de banco — `InvariantesDbTests` (24)

Cada índice parcial tem teste das **duas** metades: o que o banco recusa e o que ele tem que
aceitar. A segunda metade é a que pega um `WHERE` frouxo demais — um filtro errado passa no
teste de recusa e quebra o caso legítimo.

| Invariante | Recusa | Aceita |
|---|---|---|
| `uq_msg_wa_id` | mesmo `(instance_name, wa_message_id)` duas vezes | duas mensagens com `wa_message_id` **vazio**; duas com **NULL**; mesmo id em instâncias diferentes |
| `uq_msg_lembrete` | duas mensagens para o mesmo lembrete | várias mensagens sem lembrete |
| `uq_lembrete_teto_diario` | dois automáticos com mensagem no mesmo dia/contato | automático + manual no mesmo dia; automático após cancelar o anterior; automáticos em dias diferentes |
| `uq_conexoes_empresa` | segunda conexão na mesma empresa | uma conexão em cada empresa |
| `uq_conexoes_instance` | mesma instância em duas empresas | — |
| `uq_etapas_ganho` | segunda etapa `e_ganho` na empresa | várias etapas sem ganho; uma de ganho por empresa |
| `uq_contatos_telefone` | telefone repetido entre contatos vivos | dois contatos anonimizados com telefone zerado |
| `uq_conversas_contato` | segunda conversa para o mesmo contato | — |
| `ck_contatos_terminal` | ganho e perdido ao mesmo tempo | — |
| `ck_msg_data_disparo` | saída sem `data_disparo` | entrada sem `data_disparo` |
| `ck_lembretes_texto` | `envia_mensagem` sem texto | — |
| `ck_conversas_nao_lidas` | contador negativo | — |
| FK composta | contato apontando para etapa de outro tenant; conversa para contato de outro tenant | — |
| `ordem_kanban` | — | 40 inserções no mesmo ponto médio, 40 posições distintas |

### Seed e cadastro — `CadastroEmpresaDbTests` (8)

Cria as 5 etapas na ordem certa com uma de ganho (e é a "Venda", a última); cria o dono com
senha conferível por `HashSenha`; cria a conexão com instância derivada; grava documento só
com dígitos; recusa e-mail já usado em outro tenant (com `Conflito = true`); recusa senha curta
**sem gravar nada**; dois cadastros produzem funis separados sem id em comum; e um contato novo
já encontra etapa válida logo após o cadastro — que é o ponto do seed, porque `EtapaId` é
`NOT NULL`.

### Duas coisas que os testes pegaram enquanto eram escritos

**Seis testes falharam com "esperado 2, obtido 0"** — e a causa era exatamente a armadilha que
o bloco documenta: as contagens de conferência rodavam com o contexto ainda em tenant zero, e o
filtro global devolvia vazio sem erro. Está registrado como comentário no helper
`CenarioAsync`, porque é a demonstração mais honesta de que o teste do tenant zero não é
teórico.

**`ExecuteUpdateAsync` lança `PostgresException` crua, não `DbUpdateException`** — ele manda o
UPDATE direto, sem passar pelo `SaveChanges`, então não há nada envolvendo a exceção do driver.
O teste do contador negativo agora afere `SqlState == "23514"` e o nome da constraint.

---

## 4. Decisões que tomei por conta própria

**1. Criei um serviço de cadastro, apesar de "serviços" estarem fora do escopo.** O bloco pede,
na seção SEED e nos testes, que "cadastrar empresa crie 5 etapas e o usuário dono" — não há
como testar isso sem algo que cadastre. Fiz o mínimo: `IServicoCadastroEmpresa` no Core +
`ServicoCadastroEmpresa` na Infra, sem controller e sem DTO de API. Registrado no DI para o
bloco seguinte já encontrar pronto.

**2. `uq_usuarios_id_empresa` virou UNIQUE CONSTRAINT (era índice único).** O EF só aceita
`HasPrincipalKey` apontando para uma chave do modelo, e as FKs compostas precisam disso. No
banco o efeito é o mesmo: constraint `UNIQUE` cria o índice. A migration `Dominio` faz a troca.

**3. Novo marcador `IEntidadeCriada`, com `IEntidadeAuditada` herdando dele.** `mensagens` é
log append-only e não tem `atualizado_em`, mas continua precisando de `criado_em` automático —
sem isso voltaria a auditoria manual justamente na tabela de maior escrita. O interceptor
carimba `CriadoEm` para as duas interfaces e `AtualizadoEm` só para a completa.

**4. `instance_name` derivado como `emp-{id}` quando não informado.** É um detalhe técnico da
Evolution que o cliente não deveria digitar; derivar do id da empresa é único por construção. O
campo continua aceitando valor explícito.

**5. `MapearEnums` virou método público em `ServicosInfra`.** Os testes montam o próprio data
source e precisam da mesma lista; duas listas divergem no dia em que alguém adiciona um enum, e
o sintoma seria um teste verde contra um banco configurado errado.

**6. `Conversa.UsuarioResolveu` em vez de `Conversa.ResolvidoPorUsuario`.** Duas navegações para
`Usuario` na mesma entidade (`Responsavel` e quem resolveu) precisam de nomes distintos; mesmo
caso em `Lembrete` (`Responsavel`, `UsuarioCriou`, `UsuarioConcluiu`).

Nenhuma biblioteca nova foi instalada.

---

## 5. Pendências

**`aguardando_desde` ainda não é mantido por ninguém.** A coluna, o índice parcial e a regra
estão prontos; quem escreve (`entrada` grava, `saída` limpa) chega no bloco do WhatsApp. O
comentário na entidade alerta que o INSERT da mensagem e o UPDATE da coluna precisam estar na
**mesma transação** — é o preço de materializar, e é onde o webhook do Recupera erra.

**`PaginaCursor<T>` existe sem implementação.** Só o record, como o escopo pede. A regra de que
a paginação acontece no SQL (nunca em memória, como `ServicoInbox.ConversasAsync` do Recupera)
está registrada no comentário do tipo.

**Sem índice de FK.** Consequência consciente da remoção da convenção (§2). Se surgir consulta
que filtre por uma FK sem `empresa_id` na frente, o índice tem que ser declarado à mão.

**`Contato` não tem histórico de movimentação entre etapas.** `ganho_em`/`perdido_em` são
colunas, conforme a especificação; histórico é fase 2.

**Ainda não há como criar a primeira empresa pela API** — o serviço existe, o endpoint não
(pendência herdada do bloco 1, agora meio caminho andado).

---

## 6. Critérios de pronto — evidências

| # | Critério | Resultado |
|---|---|---|
| 1 | `dotnet build` limpo | **0 Aviso(s), 0 Erro(s)** |
| 2 | Migration equivalente à especificação | conferida objeto a objeto — §2 |
| 3 | `dotnet ef database update` em banco limpo | `Inicial` + `Dominio` aplicadas sem erro em base recém-criada |
| 4 | `dotnet test` verde | **58 aprovados, 0 falhas** (banco de teste derrubado antes, para provar que a fixture se provisiona sozinha) |
| 5 | Nenhum endpoint novo | `Controllers/` só tem `AuthController`; 2 atributos de rota no projeto inteiro, ambos do bloco 1 |

---

## 7. Observação fora do escopo

Durante este bloco, `recupera/src/Recupera.Api/Controllers/ReguaController.cs` e
`recupera/src/Recupera.Core/Motor/MotorReguaCobranca.cs` apareceram modificados no `git status`
(+31 linhas). **Não fui eu** — toda escrita deste bloco ficou em `nexora/`, e os arquivos foram
tocados minutos antes da conferência final, em paralelo. Registro só para você saber que a
árvore de trabalho do Recupera está suja com alterações não commitadas.
