# ARQ-1 — Revisão de arquitetura e funil configurável

Quatro perguntas, quatro respostas. Duas viraram código, duas viraram decisão registrada.

---

## 1. Controllers empilhados — separados

Dois arquivos tinham mais de um controller. Os outros 17 já eram 1:1.

| antes | depois |
|---|---|
| `EquipeController.cs` (4 controllers, 144 linhas) | `EquipeController.cs`, `ContaController.cs`, `ConviteController.cs`, `RedefinicaoController.cs` |
| `CapturaController.cs` (2 controllers) | `CapturaController.cs`, `FormulariosController.cs` |

Os `record` de request foram junto do controller que os usa. `DefinirSenhaRequest` ficou em
`ConviteController.cs` porque é o primeiro dos dois fluxos que o usa, com nota explicando que a
redefinição por link compartilha.

### Namespace: um só, `Nexora.Api.Controllers`

A pergunta pedia "cada controller seu namespace". Fiz **arquivos** separados, não namespaces.

Namespace em C# **não é fronteira de acesso** — `internal` e `public` são. Um namespace por
controller obrigaria um `using` a mais em cada teste e em cada referência cruzada, e não
compraria isolamento nenhum. O ganho da separação está no arquivo: achar `ContaController` pelo
nome do arquivo, e não saber de cor que ele mora dentro do de equipe.

Se a intenção era namespace literal, é uma linha por arquivo — diga e eu troco.

---

## 2. Serviços no projeto de API — ficam onde estão

São exatamente dois, e ambos são `BackgroundService`:

- `Nexora.Api/Servicos/AgendadorFollowUp.cs`
- `Nexora.Api/Servicos/FilaSegundoPlano.cs`

**Não movi, e a razão é medível.** Hoje o `Nexora.Core` referencia **um único pacote**:
`Microsoft.Extensions.Logging.Abstractions`. `BackgroundService` vem de
`Microsoft.Extensions.Hosting`. Mover esses dois arrastaria o hosting inteiro para dentro do
projeto de domínio — trocaria um incômodo estético por um acoplamento real.

O corte já está feito do jeito certo, e vale registrar como padrão:

> **A interface do que a aplicação precisa mora no Core. A implementação que depende do host mora
> no host.** `IFilaSegundoPlano` está em `Nexora.Core`; quem sabe o que é `Channel` e
> `BackgroundService` é a API.

Referências atuais, para não perder de vista:

```
Nexora.Core  → Microsoft.Extensions.Logging.Abstractions
Nexora.Infra → Nexora.Core + Npgsql.EntityFrameworkCore.PostgreSQL + Microsoft.Extensions.Http
Nexora.Api   → Nexora.Core + Nexora.Infra + JwtBearer + Swashbuckle
```

Nenhum `using Microsoft.EntityFrameworkCore` no `Nexora.Core`. Isso é o que precisa continuar
verdadeiro.

---

## 3. Repositórios — recomendação de NÃO criar

Registrado como decisão, com os motivos, para não ser reaberto sem argumento novo.

### Por que não

**O argumento clássico não se aplica aqui.** Repositório costuma ser justificado por "para poder
mockar o banco no teste". Este projeto decidiu não mockar o banco: os 441 testes rodam contra
PostgreSQL de verdade, em transação, e o PI-3 proibiu provider in-memory explicitamente. A camada
criaria ~21 interfaces que nada precisa substituir.

**O que os serviços fazem não cabe numa interface de repositório sem vazar:**

- `ServicoSerie` roda SQL bruto com window function — a reformulação que levou uma consulta de
  >30 s para 182 ms
- `ServicoDashboard` faz `GroupBy` traduzido para SQL, porque agregação em memória foi proibida
- `ServicoCaptura` precisa de `IgnoreQueryFilters()` **e** filtro explícito por tenant
- `ExecuteUpdateAsync` para contador atômico, `xmin` para concorrência otimista

Um repositório ou expõe `IQueryable` — e aí não abstraiu nada — ou precisa de um método
`SerieDeRespostasPorDia(...)`, que é o serviço com um passo a mais.

**E há um risco concreto, não teórico:** esconder o `DbContext` esconde junto o *query filter*
global. A armadilha do tenant zero (`EmpresaId == 0` devolve vazio em silêncio) fica **mais**
perigosa quando o comportamento do filtro é invisível.

### O que escala no lugar

O único caso real de duplicação de regra que apareceu — o predicado de contagem do funil copiado
em `ServicoFunil` e `ServicoDashboard` — foi resolvido no PI-6 assim:

```csharp
public static Expression<Func<Contato, bool>> NoQuadro =>
    c => c.PerdidoEm == null && c.AnonimizadoEm == null;
```

Uma `Expression` em Core, usada em 7 lugares, traduzida para SQL em todos. Dá a garantia que se
espera do repositório — uma definição só — sem a camada de encaminhamento.

Os três padrões que sustentam o crescimento:

| padrão | quando | exemplo |
|---|---|---|
| Especificação em Core | a regra é compartilhada entre serviços | `RegrasContato.NoQuadro` |
| Interface de dados estreita | a regra é pura e não pode ver EF | `IDadosFollowUp`, `IDadosMensagem` |
| Serviço próprio | consulta pesada com SQL específico | `ServicoSerie` |

### Quando reabrir

Gatilhos concretos, não teóricos:

1. **Segundo armazenamento de leitura** (dashboard num read model, busca em Elastic) — a interface
   entra só para aquele caminho
2. **Modelo de domínio rico separado do de persistência** — agregados com invariantes próprias,
   mapeados para tabelas. Hoje entidade e tabela são a mesma coisa
3. **Bancos separados por tenant**

### O que de fato limita a escala hoje

Nenhum dos três é acesso a dados:

1. **`AgendadorFollowUp` não tem lock distribuído.** Duas instâncias drenam a mesma outbox → a
   mesma mensagem de WhatsApp sai duas vezes. Na rota não-oficial, isso é denúncia.
2. **Rate limit em memória.** Duas instâncias = teto dobrado, agora num endpoint público de
   escrita (a captação).
3. **`ConfiarProxyReverso` desligado atrás de proxy** faria todos os visitantes compartilharem o
   IP do balanceador.

---

## 4. Editar entidade — o buraco era outro

**Empresa já era editável:** `PUT /api/configuracao/empresa`, com formulário na tela de
Configurações. Também eram: contato, usuário da equipe, minha conta, atendimento e formulário de
captação.

O que faltava:

| entidade | tinha | status |
|---|---|---|
| **Etapas do funil** | nada | **feito neste bloco** |
| Feriado | criar, apagar | pendente (ver abaixo) |
| Lembrete | criar, concluir, cancelar | pendente (ver abaixo) |

As etapas eram o buraco de verdade: semeadas na criação da empresa e nunca mais alteráveis. Um
CRM em que o dono não pode renomear "Novo Lead" nem acrescentar uma etapa serve à primeira
empresa e a mais nenhuma.

---

## O funil configurável

### Arquivos

| arquivo | o que é |
|---|---|
| `Core/Servicos/IServicoEtapas.cs` | contrato + `EtapaDto`, `NovaEtapa`, `EditarEtapa` |
| `Infra/Servicos/ServicoEtapas.cs` | as invariantes |
| `Api/Controllers/EtapasController.cs` | `/api/etapas`, só dono |
| `nucleo/servicos/etapas.servico.ts` | HTTP |
| `paginas/etapas/` | a tela |

**Sem migration.** A tabela `etapas_funil` já tinha tudo — `ordem`, `cor`, `e_ganho` e os índices.
Ela foi desenhada no bloco 1 para funil configurável ("funil configuravel na fase 2 nao vai exigir
migracao de dados", diz o comentário da entidade). Era verdade.

### As quatro invariantes

Três o banco garante, uma não:

| # | invariante | quem garante |
|---|---|---|
| 1 | `(empresa_id, ordem)` único | `uq_etapas_ordem` — **índice**, não constraint |
| 2 | no máximo uma etapa de ganho | `uq_etapas_ganho` (parcial) |
| 3 | não apagar etapa com contato | `fk_contatos_etapa ON DELETE RESTRICT` |
| 4 | **sobrar ao menos uma etapa não-ganho** | **só a aplicação** |

### Por que reordenar precisa de duas passadas

`uq_etapas_ordem` é um **índice** único, e no Postgres índice **não é adiável** — só `CONSTRAINT`
é. Numa troca A↔B, o `UPDATE` que chega primeiro colide com a linha que ainda não se moveu, e o
erro sai como `duplicate key value violates unique constraint` para quem só arrastou uma coluna.

A solução: estacionar todo mundo em **ordem negativa**, depois trazer de volta.

```csharp
for (var i = 0; i < idsNaOrdem.Count; i++)
    porId[idsNaOrdem[i]].Ordem = (short)-(i + 1);
await db.SaveChangesAsync(ct);

for (var i = 0; i < idsNaOrdem.Count; i++)
    porId[idsNaOrdem[i]].Ordem = (short)(i + 1);
await db.SaveChangesAsync(ct);
```

Negativos são únicos entre si e não colidem com nenhum positivo existente — a passada é segura
qualquer que seja a ordem em que o EF emita os `UPDATE`s. As duas dentro da mesma transação.

**Alternativa descartada:** trocar o índice por constraint `DEFERRABLE`. Custaria uma migration e
enfraqueceria a checagem no resto do sistema para resolver um caso que duas passadas resolvem sem
tocar no schema.

O mesmo problema, em escala menor, existe no `uq_etapas_ganho`: marcar a nova antes de desmarcar a
antiga viola. Mesma solução, mesma transação.

### A invariante nº 4, que é a perigosa

**O lead novo entra na etapa de MENOR ordem** — o que chega pelo WhatsApp e o que vem dos
formulários do site. Se a única etapa restante for a de ganho, **todo contato criado nasce ganho**.
A "porta única do ganho" (`MoverAsync` recusa a etapa `e_ganho`) cairia por dentro, sem erro em
lugar nenhum.

O banco não tem como saber disso. A guarda é explícita, e é a única das quatro que só existe em
código.

### A contagem de contatos é CRUA, e não a do quadro

`EtapaDto.Contatos` **não** usa `RegrasContato.NoQuadro`. O quadro esconde perdido e anonimizado,
mas as duas linhas continuam com `etapa_id` apontando para a etapa — e é isso que a FK enxerga.

Contar como o kanban conta mostraria "0 contatos" numa etapa que o banco recusa apagar, e o dono
levaria o erro **depois** do clique, como 500. O número aqui responde "o que trava a remoção",
não "o que aparece no funil". A tela diz isso em texto.

### Outras decisões

- **Apagar exige destino**, nunca cascata. Contato é o ativo do cliente; apagar uma coluna do
  kanban não pode significar perder as pessoas que estavam nela.
- **Renomear a etapa de ganho é permitido.** A flag `e_ganho` existe justamente para a conversão
  não depender do nome — é o que deixa a empresa chamar "Venda" de "Contrato assinado".
- **Marcar outra como ganho é operação própria**, não um campo do formulário de edição: muda o
  significado de todo o histórico de conversão.
- **Cor só aceita `#RRGGBB`.** Ela vai direto para o `style` do cabeçalho da coluna; texto livre
  seria deixar o dono escrever CSS na tela de todo mundo da empresa dele.
- **Teto de 12 etapas.** Não é limite técnico: um quadro com mais colunas deixa de responder onde
  o negócio está.
- **Reordenar recebe a lista completa**, não "sobe uma posição" — a operação fica idempotente, e
  duplo clique ou retry de rede não andam com a coluna.
- **Setas, não arrastar.** O quadro do funil já tem arrastar-e-soltar e ele nunca foi exercitado
  em navegador; repetir a mecânica numa tela de configuração somaria risco. Setas também
  funcionam com teclado.

---

## Testes

**Backend — `EtapasDbTests.cs`, 17 casos.** Os que carregam peso:

| teste | o que prova |
|---|---|
| `REORDENAR_TROCA_POSICOES_SEM_VIOLAR_O_INDICE_UNICO` | inverte o funil inteiro contra o índice real |
| `LISTA_PARCIAL_DE_ORDEM_E_RECUSADA` | lista curta, id repetido e id de fora — os dois últimos com a contagem certa |
| `MOVER_O_GANHO_NAO_DEIXA_DUAS_ETAPAS_MARCADAS` | `uq_etapas_ganho` |
| `O_FUNIL_NAO_PODE_FICAR_SO_COM_A_ETAPA_DE_GANHO` | a invariante nº 4 |
| `APAGAR_ETAPA_COM_CONTATOS_EXIGE_DESTINO_E_MOVE_TODOS` | nenhum contato se perde |
| `A_CONTAGEM_INCLUI_PERDIDO_PORQUE_E_ELE_QUE_TRAVA_A_FK` | marca todos como perdidos e confirma que a contagem **não** cai |
| `ETAPA_DE_OUTRA_EMPRESA_NAO_E_ALCANCAVEL` | editar, apagar e marcar ganho, os três |

**Frontend — `etapas.spec.ts`, 10 casos.** Inclui o que só a tela faz: a ordem inteira sendo
enviada, o destino pré-selecionado ser a etapa anterior, e a tela **voltar à verdade do servidor**
quando a API recusa a reordenação — sem isso ela ficaria mostrando uma ordem que o banco não tem.

### Mutação

Duas proteções testadas removendo-as:

| mutação | resultado |
|---|---|
| tirar a passada de ordem negativa | `REORDENAR...` e `Reordenar_e_idempotente` reprovam (`duplicate key`) |
| desligar a guarda da última etapa não-ganho | `O_FUNIL_NAO_PODE_FICAR_SO_COM_A_ETAPA_DE_GANHO` reprova |

Ambas revertidas.

### Uma falha minha que os testes pegaram

Escrevi os testes assumindo 5 etapas; o semeador de teste cria 3. Quatro reprovaram. A correção
não foi trocar 5 por 3 — foi tornar os testes **agnósticos à contagem**, porque amarrar um teste
de ordenação ao número de etapas do semeador faz a próxima mudança lá reprovar um teste que não
tem nada a ver com o assunto.

---

## Verificação ao vivo

API contra `nexora_dev`, tenant de demonstração, caminho HTTP completo:

```
--- funil inicial ---
  1. Novo Lead            contatos=20  ganho=False
  ...
  5. Venda                contatos=12  ganho=True

Inverter ordem                 -> 204   (5 posições trocadas de uma vez)
Criar etapa                    -> 200   {"id":26}
Nome repetido                  -> 400   Já existe uma etapa chamada "visita agendada".
Cor inválida                   -> 400   Cor inválida: "red;background:url(x)".
Apagar ganho                   -> 400   Esta é a etapa de ganho do funil e não pode ser apagada.
Apagar c/ contatos sem destino -> 400   Esta etapa tem 20 contatos. Escolha para qual etapa eles vão.
Apagar vazia                   -> 204
```

O funil da demonstração voltou ao estado original — confirmado no banco depois de parar a API.

**Build e testes:** `dotnet build -warnaserror` limpo, `ng build` limpo,
**441 testes de backend** e **116 de frontend** verdes.

---

## Pendências

1. **Feriado sem edição.** Erro de digitação no nome exige apagar e recriar. `PUT` simples.
2. **Lembrete sem edição.** Data errada exige cancelar e recriar, o que perde o histórico.
3. **A tela de etapas não mostra prévia do quadro.** O dono escolhe a cor num seletor e só vê o
   resultado indo ao funil.
4. **Reordenar não tem trava de concorrência.** Dois donos reordenando ao mesmo tempo: o último
   ganha, sem aviso. Cenário improvável (é tela de dono, e a empresa tem um), mas real. `xmin` na
   etapa resolveria.
5. **`ServicoCadastroEmpresa.EtapasPadrao` continua chumbado** — e deve continuar: é o ponto de
   partida, não a regra. Uma tabela de "modelos de funil" só faria sentido com mais de um modelo
   para escolher. O comentário de lá, que ainda dizia "funil configuravel e fase 2", foi
   atualizado neste bloco.
