# Bloco 7 — API de contatos e funil

Estado: **fechado**. Todos os critérios de pronto verificados, incluindo o que fecha o buraco
encontrado na varredura.

`dotnet build` limpo, **248 testes verdes** (208 antes deste bloco → 40 novos).

---

## 1. O buraco, confirmado e fechado

Antes deste bloco a única escrita de contato no sistema era a criação automática pelo webhook, em
`ProcessadorEventoEvolution.cs:282`. Nada preenchia `ganho_em`, `perdido_em`, `valor` ou
`ordem_kanban`.

O sintoma era o dashboard: ele lê essas colunas desde o bloco 6, e "vendas do mês" mostrava zero
não por falta de vendas, mas porque **nenhum caminho do produto conseguia registrar uma**.

Verificado sobre HTTP, com a API de pé contra o `nexora_dev`:

```
POST /api/contatos            -> id 2
POST /api/contatos/2/ganho    -> 204
GET  /api/dashboard           -> vendasDoMes=1  faturamento=1250.50  conversao=1
```

O teste `O_DASHBOARD_SAI_DO_ZERO_DEPOIS_DE_MARCAR_UM_GANHO` amarra as duas pontas contra Postgres
real: mede o dashboard antes (tudo zero), marca o ganho, mede depois. É o critério nº 3 do prompt.

---

## 2. O que foi criado

| Arquivo | O que é |
|---|---|
| `Core/Servicos/IServicoContatos.cs` | DTOs (`ContatoResumo`, `ContatoCard`, `ContatoDetalhe`, `NovoContato`, `EditarContato`, `FiltroContato`) e contrato |
| `Infra/Servicos/ServicoContatos.cs` | Cadastro, edição, listagem, detalhe, ganho, perda, reabertura, anonimização |
| `Core/Servicos/IServicoFunil.cs` | DTOs (`ColunaFunil`, `QuadroFunil`, `MoverContato`) e contrato |
| `Infra/Servicos/ServicoFunil.cs` | Quadro paginado, coluna por cursor, cálculo de posição e renormalização |
| `Api/Controllers/ContatosController.cs` | 7 rotas |
| `Api/Controllers/FunilController.cs` | 3 rotas |
| `tests/.../ContatosDbTests.cs` | 24 testes |
| `tests/.../FunilDbTests.cs` | 16 testes |

**Nenhuma tabela criada, nenhuma migration, nenhuma alteração de schema.** Todas as colunas já
vieram no bloco 2.

### Endpoints

```
GET    /api/contatos                     lista: filtro, busca, etapa, responsável, página
GET    /api/contatos/{id}                detalhe + conversa + lembretes
POST   /api/contatos                     criar
PUT    /api/contatos/{id}                editar
POST   /api/contatos/{id}/ganho          registrar venda (exige valor)
POST   /api/contatos/{id}/perda          marcar perdido (exige motivo)
POST   /api/contatos/{id}/reabrir        desfaz ganho ou perda
POST   /api/contatos/{id}/anonimizar     LGPD — só dono e gestor

GET    /api/funil                        quadro: etapas + contagem + soma + N cards por coluna
GET    /api/funil/etapas/{id}/contatos   mais cards de uma coluna, por cursor
POST   /api/funil/{contatoId}/mover      move ou reordena
```

Só a anonimização é restrita por papel (dono e gestor): apagar PII é irreversível. O resto é
atendimento, e o vendedor precisa disso o dia inteiro.

---

## 3. A porta única do ganho

A decisão de produto do prompt exige que arrastar o card para a coluna "Venda" e clicar em "venda
fechada" sejam **a mesma escrita**. Implementada em duas metades:

1. **`MoverAsync` recusa** qualquer etapa com `e_ganho = true`, com 409 e mensagem que orienta.
   Verificado sobre HTTP:
   `{"erro":"Para mover para a etapa de venda, registre a venda com o valor fechado."}`

2. **`MarcarGanhoAsync` também move** o card para a etapa de ganho, além de carimbar `ganho_em` e
   `valor`. Sem isso, a recusa acima deixaria a coluna "Venda" inalcançável.

Sem as duas metades, existiria contato na coluna Venda sem `ganho_em` — o card na tela e a venda
ausente do relatório, porque o dashboard conta por `ganho_em`.

Duas consequências que decidi por conta própria:

- **`PUT /api/contatos/{id}` não aceita etapa.** Aceitar abriria um terceiro caminho, sem cálculo
  de ordem e sem a recusa — exatamente o buraco que este bloco fecha.
- **Reabrir tira o card da coluna de venda** e o devolve à primeira etapa. Deixá-lo lá produziria
  card na coluna Venda sem `ganho_em`, que é o estado que a porta única existe para impedir.

---

## 4. `ordem_kanban` — e uma divergência entre o prompt e o schema

O cálculo é o do prompt, com as três bordas:

| Caso | Ordem |
|---|---|
| Entre dois cards | `(anterior + posterior) / 2` |
| Topo da coluna | `primeira - 1` |
| Fim da coluna | `última + 1` |
| Coluna vazia | `0` |

**A divergência:** o prompt diz que a coluna é `numeric(18,6)` e que a precisão esgota em ~20
movimentos. Conferi no banco — a coluna é `numeric` **sem escala**:

```
column_name  | data_type | numeric_precision | numeric_scale
ordem_kanban | numeric   |                   |
```

Foi decisão deliberada do bloco 2, e o comentário em `Contato.OrdemKanban` explica: com
`numeric(18,6)` o ponto médio esgota em ~19 movimentos e dois cards colidem; `numeric` puro vai a
milhares de casas decimais. Na prática o limite passa a ser o `decimal` do C#, com ~28 dígitos
significativos — mais de 90 divisões no mesmo par.

**Implementei a renormalização assim mesmo**, com o limiar de `0.000002` que o prompt pede. Não é
zelo mal calibrado: renormalizar é barato (um UPDATE por linha, numa operação já interativa e
rara) e mantém as ordens em números legíveis. Depurar uma coluna cujos valores são
`3.0000019073486328125` é bem pior do que renormalizar cedo demais.

A renormalização reescreve a coluna como 1, 2, 3… ordenando por `(ordem_kanban, id)` — o **mesmo**
par da leitura do quadro. Sem o `id` no desempate, dois cards com ordem igual sairiam invertidos e
o vendedor veria os cards trocarem de lugar depois de arrastar um deles.

Card perdido **não entra** na renormalização: ele está fora do quadro pelo índice parcial
`ix_contatos_kanban`, e renumerá-lo o faria voltar a competir por posição. Há teste.

---

## 5. Anonimização LGPD

Zera `nome`, `email`, `observacoes`, `origem_detalhe` e `telefone`. Preserva etapa, ordem, valor,
`ganho_em`, `perdido_em`, `motivo_perda`, responsável — e, por não serem tocadas, a conversa, as
mensagens e os lembretes. Sem delete físico, sem soft delete.

**O telefone.** É NOT NULL, então não dá para apagar. O substituto é `ANON-{id}`: determinístico
(reexecutar é idempotente) e único (o id é único).

Uma observação que a implementação revelou e vale registrar: **a colisão entre dois anonimizados
não ocorreria nem sem o marcador**. O índice `uq_contatos_telefone` é parcial, com
`WHERE anonimizado_em IS NULL`, e a linha sai do índice no instante em que é anonimizada. O
marcador não está lá para satisfazer a constraint — está porque o telefone **é** a PII, e apagá-lo
é o ponto da operação.

Esse mesmo índice parcial tem outra consequência que exigiu cuidado: a checagem de telefone
duplicado no cadastro precisa **repetir o predicado** (`AnonimizadoEm == null`). Sem isso, um
contato anonimizado bloquearia o cadastro de um contato novo com o mesmo número, e a mensagem de
erro seria uma mentira. Há teste.

---

## 6. Paginação — e uma contradição no prompt

O prompt diz duas coisas incompatíveis sobre a lista de contatos: na tabela de endpoints,
"paginação por cursor"; na seção PAGINAÇÃO, "`Pagina<T>` para listas estáveis (contatos)".

**Segui a segunda**, e o motivo é técnico: cursor existe para lista que se **reordena sozinha**
entre requisições — na caixa de entrada, conversa nova sobe para o topo enquanto o vendedor rola.
A lista de contatos é ordenada por nome, e contato não muda de nome sozinho. Offset é seguro ali,
e dá o total ("142 contatos") que a lista precisa e que cursor não fornece.

Onde cursor **é** necessário e foi usado: a **coluna do kanban**. É literalmente a tela onde o
vendedor arrasta cards, e entre duas páginas a coluna pode ter sido reordenada. O cursor é o par
`(ordem_kanban, id)`, o mesmo do índice `ix_contatos_kanban`.

**Tudo no SQL:** filtro, busca, `COUNT`, `SUM`, ordenação e corte. O que acontece em memória é só
remontagem de campo depois do `ToListAsync` — nada de filtro, ordenação ou agregação.

O quadro carrega **N cards por coluna** (padrão 50, teto 200), com `Total` e `ValorTotal`
agregados sobre o conjunto **inteiro**, não sobre a página. Uma empresa com 3.000 leads em "Novo
Lead" não derruba a tela. Há teste.

---

## 7. Multi-tenant

O query filter global cobre a leitura. O que precisou de checagem explícita, porque o id vem do
cliente:

- **etapa de destino** ao criar e ao mover;
- **responsável** ao criar e ao editar;
- **card de referência** (`AposContatoId`) — tem que estar na etapa de destino, senão o "meio"
  seria calculado entre vizinhos de colunas diferentes.

Como `db.EtapasFunil` e `db.Usuarios` já vêm filtrados pelo tenant, "não encontrado" e "é de outra
empresa" caem no mesmo ramo — que é exatamente o que um tenant deve ver do outro. Quatro testes.

---

## 8. Como cada critério foi verificado

| Critério | Teste |
|---|---|
| criar, editar, listar com busca, detalhe | `Criar_canonicaliza_o_telefone_e_entra_na_primeira_etapa`, `Editar_altera_os_dados_e_NAO_mexe_na_etapa`, `Listar_busca_por_nome_e_por_digitos_do_telefone`, `Detalhe_traz_a_conversa_e_os_lembretes_numa_chamada_so` |
| ponto médio correto | `Mover_entre_dois_cards_calcula_o_PONTO_MEDIO` |
| topo, fim e coluna vazia | `Mover_para_o_TOPO_da_coluna`, `Mover_para_o_FIM_da_coluna`, `Mover_para_COLUNA_VAZIA` |
| renormalização + ordem relativa preservada | `RENORMALIZA_quando_a_precisao_se_esgota_e_PRESERVA_a_ordem_relativa` |
| mover para `e_ganho` recusado | `MOVER_PARA_A_ETAPA_DE_GANHO_E_RECUSADO_com_mensagem_que_orienta` |
| ganho sem valor recusado | `Marcar_ganho_sem_valor_e_recusado` |
| perda sem motivo recusada | `Marcar_perdido_sem_motivo_e_recusado` |
| reabrir limpa os marcos | `Reabrir_limpa_ganho_perda_e_motivo_mas_PRESERVA_o_valor` |
| anonimizar: PII, histórico, telefone sem colidir | `Anonimizar_zera_a_PII_e_PRESERVA_o_historico`, `Dois_contatos_anonimizados_convivem_sem_colidir_no_telefone` |
| anonimizado some da lista e do kanban, conta no dashboard | `Anonimizado_some_da_lista_mas_o_telefone_dele_libera_cadastro_novo`, `Perdido_e_anonimizado_somem_do_quadro_e_da_contagem`, `Contato_anonimizado_continua_contando_no_dashboard` |
| etapa de outra empresa recusada | `Criar_com_etapa_de_OUTRA_empresa_e_recusado`, `Mover_para_etapa_de_OUTRA_empresa_e_recusado` |
| responsável de outra empresa recusado | `Atribuir_responsavel_de_OUTRA_empresa_e_recusado` |
| kanban pagina e devolve total | `Quadro_pagina_por_coluna_e_devolve_a_contagem_do_conjunto_INTEIRO`, `Coluna_pagina_por_cursor_sem_pular_nem_repetir` |
| **dashboard sai do zero** | `O_DASHBOARD_SAI_DO_ZERO_DEPOIS_DE_MARCAR_UM_GANHO` |

Mais o que não estava na lista: telefone inválido recusado, telefone repetido com 409, reordenar
dentro da mesma coluna, card de referência de outra coluna recusado, contato perdido não pode ser
moído sem reabrir, ganho sobre perdido recusado, perda preserva a etapa onde a negociação morreu,
anonimizado não aceita mais alteração, e o quadro não vaza entre tenants.

### Verificação manual sobre HTTP

Com a API de pé: as 10 rotas aparecem no Swagger, o cadastro devolve id, o quadro devolve as 5
etapas com contagem e soma, o ganho move o card e o dashboard passa a mostrar `vendasDoMes=1`. A
recusa da etapa de ganho devolve **409 com corpo** — conferi o corpo especificamente, porque um
409 vazio deixaria a tela sem a mensagem que orienta o vendedor, e a orientação é metade do ponto
da recusa.

Os dados criados no `nexora_dev` durante a verificação foram removidos.

---

## 9. Decisões próprias

1. **Contato novo entra no FIM da coluna.** Lead novo no topo empurraria para baixo o que o
   vendedor já estava trabalhando; a ordem do quadro é dele, não do sistema.
2. **Origem padrão do cadastro manual é `manual`**, não `whatsapp` (o default da coluna). Quem
   digita o contato não veio do WhatsApp.
3. **Busca de telefone pelos dígitos.** O vendedor digita `(84) 98888` e a coluna guarda
   `5584988887777`. Sem tirar a máscara, buscar pelo que está na tela não acha nada — e o vendedor
   conclui que o contato não existe. Menos de 3 dígitos não dispara busca por telefone: `"8"`
   casaria com metade da base.
4. **Telefone inválido é recusado no cadastro.** Aceitar produz o pior modo de falha do sistema: o
   contato existe, aparece na tela, e nunca casa com mensagem nenhuma — em silêncio.
5. **Ganho sobre perdido (e vice-versa) é recusado**, em vez de limpar o marco anterior por baixo
   do pano. `ck_contatos_terminal` proíbe os dois juntos; limpar em silêncio faria o histórico
   sumir sem ninguém entender por quê.
6. **Reabrir preserva `valor`.** É a estimativa do negócio, não o registro da venda.
7. **`MoverAsync` devolve a nova ordem.** O cliente pinta a posição de forma otimista ao arrastar;
   com a ordem de volta ele confere e recarrega a coluna se houve renormalização.
8. **Filtro `Abertos` como padrão da lista.** Contato ganho ou perdido é histórico; a lista de
   trabalho não deve começar cheia de coisa fechada.

---

## 10. Pendências e limites

### Deste bloco

| Limite | Consequência | Quando doer |
|---|---|---|
| Sem endpoint de **exclusão** de contato | Só anonimização (por desenho — o schema não tem delete) | Nunca; é a decisão certa para LGPD |
| Sem **mover em lote** | Arrastar 20 cards são 20 requisições | Quando alguém reorganizar o funil inteiro |
| Sem **importação** de contatos (CSV) | Cadastro é um a um | Na migração do primeiro cliente que já tem base |
| Sem **histórico** de movimentação entre etapas | Não dá para responder "quanto tempo ficou em Proposta" | Fase 2; o schema já registra isso como fora de escopo |
| `QuadroAsync` faz **uma consulta por coluna** | 5 consultas indexadas por carregamento do quadro | Com muitas etapas configuráveis (fase 2). Uma window function resolveria, mas exigiria SQL cru |
| Sem **controle de concorrência** ao mover | Dois vendedores arrastando o mesmo card ao mesmo tempo: o último ganha | Raro e de baixo impacto — a ordem é preferência visual, não dado crítico |

### Carregadas dos blocos anteriores

- **Nenhum telefone pareado** (desde o bloco 3). Segue sendo o único ponto do projeto onde pode
  aparecer surpresa em vez de trabalho.
- **Nenhum envio de e-mail** (desde o bloco 1).
- **`ServicoCadastroEmpresa` existe e tem teste, mas nenhum controller o expõe** — não há como
  criar empresa pela API.
- **Sem tela de configuração** (expediente, faixas do semáforo, dias de follow-up).
- **Sem lock distribuído** no agendador de follow-up.
- **Nenhum teste de frontend.**
- `senhas-dev.sql` na raiz do repositório, com senha em texto puro.

---

## 11. Nota sobre os documentos de referência

Dois arquivos citados no prompt não existem:

- **`docs/schema_nexora_fase1.sql`** — o arquivo real é `docs/SCHEMA-NEXORA.sql`. Usei esse.
- **`docs/ESTADO-ATUAL.md`** — a varredura foi interrompida antes de o relatório ser escrito. Os
  achados citados no prompt (a ausência da camada de escrita, o dashboard preso em zero) estão
  corretos e foram confirmados de novo aqui, mas o documento em si não chegou a existir.

O prompt também menciona "etapas 0, 0.1 e 0.2 feitas" — não encontrei registro dessas etapas em
nenhum documento do repositório, e segui sem elas.

---

## 12. O que falta para a fase 1 estar vendável

Com este bloco, o backend da fase 1 está completo. O que resta é quase todo de tela:

1. **Parear um telefone e mandar mensagem de verdade** — esforço desconhecido, e é o único item
   com risco em vez de volume. Vale fazer antes dos demais.
2. **Telas de funil, contatos (lista) e contato (detalhe)** — esforço alto. A API está pronta.
3. **Tela do dashboard** — esforço baixo. A API está pronta desde o bloco 6 e agora tem dado real.
4. **Onboarding e configurações** — esforço médio. Expor o `ServicoCadastroEmpresa` e criar a API
   e a tela de configuração da empresa.
5. **Envio de e-mail** — esforço médio.
