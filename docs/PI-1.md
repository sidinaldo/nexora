# PI-1 — Desbloqueio do build e higiene

Estado: **fechado**. Os 6 critérios verificados por execução.

`ng build` limpo, `dotnet build` limpo sem warning, **287 testes verdes**.

---

## 1. Meu Dia — o bloqueio

`meu-dia.html` e `meu-dia.css` foram reescritos para acompanhar o `meu-dia.ts`, que já estava na
versão de **lista única**. O `.ts` **não foi revertido**.

### O que a tela faz agora

- **Uma lista só**, ordenada pelo `momento` em que cada ação deveria acontecer: lembrete atrasado
  no topo (data-alvo no passado), depois conversa esperando há mais tempo, depois lembrete com
  hora marcada, e por último lembrete sem hora (tratado como "fim do dia").
- Cada linha: **horário**, **marcador**, avatar, o que fazer, contato e tempo de espera.
- **Semáforo só nas conversas**, calculado no cliente por `nucleo/semaforo.ts` a partir do
  timestamp e dos minutos úteis do servidor. Lembrete recebe um contorno neutro — hora marcada
  não é urgência, e pintá-la de vermelho diluiria o sinal.
- **Concluir remove o item** de forma otimista: some com animação de 220ms (opacidade + colapso
  de altura, casada com `MsSaida` no componente), e a chamada vai em paralelo. Se a API recusar,
  o item volta e o toast diz por quê.
- **Clicar abre o contexto**: conversa vai para `/caixa?conversa=N`, lembrete vai para
  `/contatos/{id}`.
- **Estado vazio positivo**: marca de check em verde e *"Nenhuma pendência. Tudo em dia."*
- Aviso discreto quando está fora do expediente, explicando por que os alertas não acendem.

### Um segundo bug, encontrado ao verificar

O build voltou, mas a tela **ainda não funcionaria**. A API enviava `tipo` em **PascalCase**:

```
tipo='Responder'   tipo='Lembrete'
```

e o cliente compara com `'responder'` / `'lembrete'`. A comparação nunca casava, e o efeito era
**silencioso**:

| Consequência | Por quê |
|---|---|
| Semáforo **nunca colorido** | `urgencia()` sai por `a.tipo !== 'responder'` → sempre `'baixa'` |
| Toda ação renderizada como lembrete | `@if (a.tipo === 'responder')` nunca verdadeiro |
| **Botão "Concluir" em conversa**, chamando `/api/lembretes/{conversaId}/concluir` | O template não distinguia os dois |

Isso está **dentro do escopo** — o critério 3 exige semáforo colorido e concluir funcionando.

**A causa:** `AcaoDoDia.Tipo` era o **único** DTO do sistema que saía como enum cru. Todos os
outros (`Papel`, `Status`, `Direcao`, `Origem`, `TipoMidia`, `Abrangencia`) passam por
`.ToString().ToLower()` no serviço. Com o enum cru, o `JsonStringEnumConverter` serializa o nome
do membro — PascalCase.

**A correção seguiu o padrão da casa**, em vez de adaptar o cliente ao outlier:

| Arquivo | Mudança |
|---|---|
| `Core/Servicos/IServicoMeuDia.cs` | `AcaoDoDia.Tipo` passou de `TipoAcao` para `string` |
| `Infra/Servicos/ServicoMeuDia.cs` | `TipoAcao.Responder.ToString().ToLower()` na projeção em memória; literal `"lembrete"` na projeção traduzida para SQL (o EF não traduz `ToString()` sobre constante de enum) |
| `tests/.../MeuDiaDbTests.cs` | Duas asserções passaram a comparar com as strings |

Verificado sobre HTTP depois da correção:

```
tipo='responder'  titulo='Responder Joana Ferreira'  minutosUteis=106
tipo='lembrete'   titulo='Ligar para fechar'
```

### Verificação do critério 3

Sem navegador nesta sessão, verifiquei o payload e o comportamento da API que a tela consome:

```
1. VAZIO           acoes=0  -> a tela cai no estado "Nenhuma pendência. Tudo em dia."
2. LISTA ÚNICA     2 ações vindas das DUAS fontes no mesmo array:
                     responder  'Responder Joana Ferreira'  minutosUteis=103
                     lembrete   'Ligar para fechar'         hora=14:30:00
3. CONCLUIR        antes=2  depois=1  -> o lembrete saiu
```

**O que não foi verificado por renderização:** as cores, a animação de saída e o arrasto de
foco. Conferi os dados e a lógica, não os pixels.

---

## 2. `senhas-dev.sql` — removido

Arquivo de 89 bytes na raiz, com senha em texto puro de dois usuários de desenvolvimento
(`ana@padaria.com` e `carlos@padaria.com`). **Removido.**

**Sobre o histórico:** o Nexora **não é um repositório git** — `git rev-parse` responde
`fatal: not a git repository`. Não há histórico de onde purgar, e o arquivo nunca foi commitado.
Quando o repositório for inicializado, ele já não existirá.

Não virou seed em código: o `nexora_dev` já tem os dois usuários criados, e o
`ServicoCadastroEmpresa` (que cria empresa + dono com senha informada) é o caminho de cadastro
quando alguém precisar recriar o ambiente. Um seed novo aqui seria código sem chamador.

---

## 3. Fábrica de design-time — falha alto

`FabricaDbContextDesignTime` tinha como padrão
`Database=nexora;Username=postgres;Password=postgres`. O de desenvolvimento é `nexora_dev`
(user-secrets). Rodar `dotnet ef database update` sem `NEXORA_CONN` criava um banco vazio com o
schema aplicado, **sem erro nenhum** — foi exatamente o que aconteceu no bloco 6.

**O padrão foi removido.** Sem `NEXORA_CONN`, a fábrica lança `InvalidOperationException` com o
comando exato para PowerShell e para bash. Verificado:

```
$ dotnet dotnet-ef database update --project src/Nexora.Infra --startup-project src/Nexora.Api

Unable to create a 'DbContext' of type ''. The exception 'NEXORA_CONN nao esta definida.
  Use a MESMA string do user-secrets do projeto. Para consultar:
      dotnet user-secrets list --project src/Nexora.Api
  PowerShell:
      $env:NEXORA_CONN = "Host=localhost;Port=5432;Database=nexora_dev;Username=postgres;Password=..."
  bash:
      export NEXORA_CONN='Host=localhost;Port=5432;Database=nexora_dev;Username=postgres;Password=...'
  ...' was thrown while attempting to create an instance.

$ psql -c "SELECT datname FROM pg_database WHERE datname LIKE 'nexora%'"
 nexora_dev
 nexora_teste
```

Nenhum banco criado. O raciocínio ficou registrado no cabeçalho do arquivo: **default silencioso
que aponta para o banco errado é pior que erro** — o erro se conserta em dez segundos, o banco
errado custa meia hora de depuração.

---

## 4. Banco órfão — dropado

Confirmado vazio **antes** de dropar:

```
 empresas | usuarios | contatos | conversas | mensagens | lembretes | feriados
        0 |        0 |        0 |         0 |         0 |         0 |        0
```

Zero conexões ativas. `DROP DATABASE nexora` executado. Restaram `nexora_dev` e `nexora_teste`.

---

## 5. Varredura de segredos versionados

### Nada crítico encontrado

| Onde | O que tem | Avaliação |
|---|---|---|
| `src/Nexora.Api/appsettings.json` | Emissor JWT, URL da Evolution, seção `Email` sem credencial, origens CORS | **Limpo.** Nenhum segredo |
| `src/Nexora.Api/appsettings.Development.json` | Só níveis de log | **Limpo** |
| `frontend/.../environments/*.ts` | `apiBase` e `hubBase` | **Limpo.** Produção usa caminho relativo |
| `docker-compose.yml` | `${POSTGRES_PASSWORD:?…}`, `${EVOLUTION_API_KEY:?…}` | **Limpo.** Interpolação com erro obrigatório, sem literal |

### Registrado, sem corrigir

| Item | Situação |
|---|---|
| **`.env` na raiz, com valores reais** (senha do Postgres, `EVOLUTION_API_KEY`, senha do banco da Evolution) | **Coberto pelo `.gitignore`** (linhas 7, 493 e 494) — não vai para o repositório. Fica no disco por ser o arquivo local do compose, que é o desenho pretendido. Nenhuma ação |
| **`.env.example` traz `EVOLUTION_DB_PASS=evolution_dev`** preenchido | É o mesmo default do `docker-compose.yml` (`${EVOLUTION_DB_PASS:-evolution_dev}`), para o Postgres **interno** da Evolution, que não é exposto fora da rede do compose. Fica; as outras variáveis sensíveis do exemplo estão vazias ou com `SEGREDO_AQUI` |
| **Senhas em arquivos de teste** (`senha-forte-123`, `senha-de-teste-123`, `chave-de-teste-com-pelo-menos-32-caracteres`) | Fixtures de teste, não credencial de ambiente. Nenhuma ação |
| **`CHAVE_TOKEN = 'nexora.token'`** em `auth.servico.ts` | Nome de chave do `localStorage`, não segredo. Falso positivo da varredura |

**Segredos reais do sistema, por onde entram:** `ConnectionStrings:Nexora`, `Jwt:Chave`,
`Evolution:ApiKey`, `Webhook:Segredo`, `Email:Usuario` e `Email:Senha` — todos por user-secrets
em dev e variável de ambiente em produção. Nenhum aparece em arquivo versionado.

---

## 6. Achado fora do escopo — registrado, não corrigido

**Criar lembrete com hora pela tela do contato devolve 400.**

O `<input type="time">` do HTML produz `"14:30"` (sem segundos). O `TimeOnly` do
System.Text.Json exige `"14:30:00"`. Resultado:

```
POST /api/lembretes  {"horaAlvo":"14:30", …}
400  "The JSON value could not be converted to Nexora.Core.Servicos.NovoLembrete.
      Path: $.horaAlvo"

POST /api/lembretes  {"horaAlvo":"14:30:00", …}
200  {"id":2}
```

- **Onde:** `frontend/.../paginas/contato/contato.ts:281` envia `horaAlvo: this.lHora() || null`,
  e `lHora` vem direto do `<input type="time">` em `contato.html:305`.
- **Efeito:** lembrete manual **com horário** nunca é criado pela tela. Sem horário funciona.
- **Correções possíveis:** normalizar no cliente (`lHora() ? lHora() + ':00' : null`) ou aceitar
  `HH:mm` no servidor com um `JsonConverter` de `TimeOnly`.

Não corrigido por estar fora do escopo do PI-1, conforme a instrução.

---

## 7. Pendências

### Do Meu Dia

| Limite | Consequência |
|---|---|
| **Não renderizado em navegador** | Cores, animação e alinhamento conferidos por leitura, não por olho |
| **Sem "horário sugerido" para conversa** | A API não tem esse campo; a linha mostra desde quando o cliente espera |
| **Sem subtipo de tarefa** (ligar / enviar proposta) | `TipoAcao` só distingue conversa de lembrete. O verbo vem do título que a pessoa escreveu |
| **Realtime recarrega a lista inteira** | Funciona e se reordena sozinho; não há atualização item a item |

### Carregadas

- **Nenhum telefone pareado** (desde o bloco 3) — o único item com risco em vez de volume.
- **Arrasto do kanban não testado em navegador** (bloco 8).
- **`ServicoCadastroEmpresa` sem controller** — a primeira empresa nasce por SQL ou teste.
- **Sem endpoint de série temporal** — o gráfico do dashboard só roda no modo demonstração.
- **Sem lock distribuído** no agendador de follow-up.
- **Nenhum teste de frontend** — Karma e Jasmine instalados, sem specs.
- **Sem CI.**
- **Sem SPF/DKIM/DMARC** documentados para o domínio remetente.
