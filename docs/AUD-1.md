# AUD-1 — Trilha de auditoria

## Como a captura funciona

**Os serviços declaram a ação; o interceptor preenche o resto.**

O interceptor vê `ganho_em` indo de NULL para uma data e **não tem como saber** se foi "o vendedor
fechou a venda", "a migração carimbou" ou "o suporte corrigiu". Adivinhar produziria trilha que
mente com confiança — pior que trilha ausente, porque parece confiável.

```
serviço → trilha.Declarar(Contato, id, Ganhou)
                 ↓
InterceptorTrilha (no SavingChanges)
   lê o ChangeTracker → monta o diff
   lê o contexto      → autor, tenant, instante
   ACRESCENTA a linha de `auditoria` ao mesmo SaveChanges
```

As linhas entram no **mesmo comando e na mesma transação**: ou o fato e a trilha dele existem, ou
nenhum dos dois. Gravar depois abriria a janela em que a venda foi cancelada e ninguém registrou
quem.

**Sem declaração não há linha.** É deliberado: escrita que ninguém declarou — `nao_lidas` subindo a
cada mensagem, um contador do webhook — não é evento de auditoria. Auditar tudo por padrão e ir
excluindo encheria a tabela de ruído e deixaria a linha do tempo do contato ilegível no primeiro
dia.

**Uma linha por EVENTO, não por campo.** "Editei o contato" é um fato; virar seis linhas soltas
faria o leitor remontar na cabeça o que foi um clique só.

---

## O que ficou auditado

| Entidade | Ações | Como |
|---|---|---|
| `contatos` | criou, editou, moveu, ganhou, perdeu, reabriu, anonimizou | declarado em `ServicoContatos` e `ServicoFunil` |
| `vendas` | criou, cancelou | `ServicoContatos.MarcarGanhoAsync`, `ServicoVendas.CancelarAsync` |
| `conversas` | atribuiu (assumir/liberar) | `ServicoConversas` — **só** essas duas |

`moveu` grava os **nomes** das etapas, não os ids: o serviço acabou de lê-los, e resolver ids na
tela faria o histórico mudar de texto sozinho quando alguém renomeasse ou excluísse uma etapa.

`cancelou` grava o **valor** desfeito explicitamente — o diff sozinho traria só
`canceladaEm: null → data`, e quem lê quer saber quanto sumiu da contagem.

---

## O que ficou de fora, e por quê

**`mensagens`** — append-only e imutável. Não há edição para registrar.

**`conversas` no fluxo normal** — é escrita a cada mensagem (`aguardando_desde`, `nao_lidas`,
`ultima_mensagem_*`). Auditar isso geraria mais linha de trilha que de mensagem e afogaria os
eventos que importam. Assumir e liberar entram: são decisão humana, e "quem pegou esse
atendimento" é exatamente o que a trilha existe para responder.

**Campos técnicos** — `criado_em`, `atualizado_em`, `xmin` e `ordem_kanban` estão na lista de
ignorados. Os três primeiros são mecânica; `ordem_kanban` é posição dentro da coluna, e arrastar um
card dois lugares acima não é fato de negócio — o que importa é a **mudança de etapa**, que vem
declarada.

**SQL cru** — `DadosMensagem` e `DadosFollowUp` escrevem direto e **não passam pelo interceptor**.
Foi verificado: o que elas alteram é `ack`, `enviada_em`, `expirada_em` e `nao_lidas` — nenhum é
evento de auditoria, então nada se perdeu. Se um dia alguma delas mexer em entidade auditável, terá
que declarar na mão.

**`usuarios`, `empresas`, `etapas_funil`, `conexoes` — NÃO foram ligados.** A infraestrutura está
pronta (os valores existem em `EntidadeAuditada`, e ligar cada um é uma linha de `Declarar` no
serviço correspondente), mas os serviços não declaram ainda. É a pendência principal deste bloco:
mudança de papel e desativação de usuário são exatamente o tipo de coisa que se quer auditada, e
hoje não são.

---

## LGPD — a PII na trilha

**A trilha guarda valor antigo, e valor antigo de contato é nome, telefone, e-mail e observação.**
O próprio evento de anonimização grava `nome: "João" → "Contato anonimizado"`.

Se essas linhas ficassem, **a anonimização não teria acontecido**: o dado pessoal continuaria no
banco, só teria mudado de tabela. Um pedido de titular respondido com "foi removido" seria falso.

`AnonimizarAsync` roda um `UPDATE` que reconstrói o `jsonb` chave a chave e substitui os valores
das chaves de PII por `[removido]` — **em todos** os eventos daquele contato.

- **O evento fica:** continua registrado que alguém editou o nome em tal dia, por quem. É o que
  preserva a trilha como prova de conformidade.
- **O dado sai.**
- **O que não é PII permanece legível** (valor, etapa, ganho_em). Apagar `alteracoes` inteiro seria
  mais simples e destruiria a utilidade da trilha para tudo que não é dado pessoal.

Roda **depois** do `SaveChanges`, de propósito: a linha do próprio `Anonimizou` precisa existir
para ser mascarada. Antes, o evento mais sensível ficaria intacto.

**Provado por mutação:** comentada a chamada de mascaramento, **os dois testes de LGPD falham** —
incluindo a varredura cega por texto (`alteracoes::text LIKE`), que é como o encarregado de dados
procuraria, sem saber em que chave olhar.

---

## Retenção: 12 meses

O número sai da pergunta que a trilha responde — "quem mexeu nisso e quando" — e ela é feita dias
ou semanas depois do fato, no máximo na conferência de fechamento do ano. Guardar mais é pagar
armazenamento por uma pergunta que ninguém faz; e, tratando-se de dado que inclui histórico de
pessoa, guardar além do necessário é exposição, não zelo.

O expurgo entrou na **rodada diária que já existe** (`AgendadorFollowUp`), junto com o de entregas
de webhook. Nenhum `BackgroundService` novo: ele teria que reimplementar as mesmas proteções —
catch que não deixa exceção subir, log protegido dentro do catch, fuso de negócio.

---

## Na tela

**Detalhe do contato**, seção "Histórico de alterações", em português:

```
07/08 14:32 · Ana Souza marcou venda fechada
07/08 09:10 · Ana Souza moveu de Negociação para Proposta
06/08 16:45 · Sistema cadastrou o contato
```

A tradução mora no **cliente**, a partir do `jsonb` cru. É texto de interface: muda com a redação
do produto, e traduzi-la no servidor obrigaria a um deploy de backend para corrigir uma frase.
Nome de coluna nunca aparece — há um mapa `ROTULOS` (`responsavelId` → "o responsável").

Ação do sistema fica atenuada em itálico: é contexto, não decisão de alguém, e dar-lhe o mesmo peso
faria a linha do tempo parecer mais movimentada do que foi.

**Só dono e gestor.** A checagem está no **serviço**, não num `[Authorize(Roles=...)]` do
controller — assim vale também quando outro código chamar por dentro. O controller tem `[Authorize]`
simples de propósito: duplicar a regra criaria duas fontes da mesma verdade, e a do controller é a
que se esquece de atualizar.

---

## Verificação

- `dotnet build -warnaserror` — **0 avisos**
- `dotnet test` — **619 passando** (11 novos)
- frontend — **237 passando**

| teste | o que fixa |
|---|---|
| editar grava **um** evento com todos os campos | uma linha por evento, não por campo |
| campo intocado e `atualizado_em` ficam fora do diff | o que mudou não se perde no meio do resto |
| mover grava os **nomes** das etapas | a tela não mostra `etapa_id: 4 → 3` |
| ganhou/criou-venda/reabriu/cancelou são distintos | o interceptor não adivinha; o serviço declara |
| ação sem sessão → `ator=sistema`, `usuario_id` NULL | nada de autoria falsa |
| **anonimizar remove a PII e preserva os eventos** | a anonimização acontece de verdade |
| **varredura cega pelo nome antigo não acha nada** | como o encarregado de dados verificaria |
| conversa não gera evento por mensagem | a trilha não afoga em ruído |
| assumir a conversa **gera** evento | o contrapeso: decisão humana entra |
| vendedor não acessa, gestor acessa | a asserção dupla evita regra que recusa todo mundo |
| query filter isola entre tenants | e o serviço não devolve linha alheia nem pelo id |
| expurgo remove o que passou e preserva o resto | a retenção funciona nas duas direções |

### Um bug encontrado pelo caminho

O teste de `ator = sistema` derrubou o **NEG-1**: `MarcarGanhoAsync` gravava
`ResponsavelId = contexto.UsuarioId`, e sem sessão o contexto traz **zero** — que viola
`FK_vendas_usuarios_responsavel_id` e faz o fechamento inteiro falhar. Corrigido aqui e no
`CanceladaPor` do cancelamento: **0 não é usuário**.

---

## Pendências

**Auditar `usuarios`, `empresas`, `etapas_funil` e `conexoes`.** É a maior lacuna: mudança de papel
e desativação de usuário deveriam estar na trilha desde o primeiro dia. Falta uma linha de
`Declarar` em cada serviço — a infraestrutura já aceita.

**Eventos de criação são gravados num segundo `SaveChanges`.** O id só existe depois do INSERT, e
gravar a trilha com zero produziria eventos órfãos. O custo é que, se o segundo comando falhar, o
registro existe sem o evento "criou" — o lado barato do erro, porque a criação também está em
`criado_em`, enquanto uma edição perdida não tem outra fonte.

**A trilha da empresa e a da venda não têm tela.** As rotas existem (`/api/trilha/venda/{id}`,
`/api/trilha/empresa/{id}`) e foram exercitadas pelo serviço nos testes, mas só o contato tem linha
do tempo renderizada. As Configurações continuam sem o "últimas alterações" que o prompt pediu.

**Nenhuma asserção de frontend sobre a linha do tempo** além do *smoke test* de renderização. O
`frase()` é uma função pura e seria barato testar — ficou de fora.
