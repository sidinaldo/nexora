# INT-1 — Captação por formulário do site

## O que foi criado

### Banco

`formularios_captura` — migration `20260805221143_FormulariosCaptura`, aplicada em `nexora_dev`.

| coluna | tipo | nota |
|---|---|---|
| `id` | bigint identity | |
| `empresa_id` | bigint | FK, query filter |
| `nome` | varchar(80) | vai para `contatos.origem_detalhe` |
| `chave` | varchar(64) | **`uq_formularios_chave`, único GLOBAL** |
| `dominio_permitido` | varchar(200) null | host, sem esquema |
| `ativo` | boolean | |
| `leads_recebidos` | int | incrementado no SQL |
| `criado_em` / `atualizado_em` | timestamptz | |

Índice `ix_formularios_empresa (empresa_id)` para a listagem.

**A chave é única globalmente, não por empresa.** A URL pública é `/api/captura/{chave}` e não
carrega o tenant — é a chave que o resolve. Um índice único por `(empresa_id, chave)` permitiria
a mesma chave em duas empresas e a resolução ficaria ambígua no exato caminho onde não há sessão
para desempatar.

**A chave é por FORMULÁRIO, não por empresa.** Quando uma vaza, o dono revoga aquela e o resto do
site continua captando. Chave única por empresa transformaria qualquer vazamento em parada total.

### Backend

| arquivo | o que é |
|---|---|
| `Core/Entidades/FormularioCaptura.cs` | entidade |
| `Core/Servicos/IServicoCaptura.cs` | `LeadDoFormulario`, `ResultadoCaptura`, `FormularioDto`, `NovoFormulario` e as duas interfaces |
| `Infra/Servicos/ServicoCaptura.cs` | o caminho público |
| `Infra/Servicos/ServicoFormularios.cs` | o CRUD da área logada |
| `Api/Controllers/CapturaController.cs` | `CapturaController` (público) e `FormulariosController` (dono) |
| `Api/Seguranca/RateLimitingConfig.cs` | política `captura`, `CapturaPorMinuto = 10` |
| `Api/Program.cs` | política de **CORS** `captura`, separada da do painel |

### Frontend

| arquivo | o que é |
|---|---|
| `nucleo/servicos/formularios.servico.ts` | HTTP + `urlPublica()` |
| `nucleo/modelos.ts` | `FormularioDto` |
| `paginas/formularios/` | a tela (`.ts`, `.html`, `.css`, `.spec.ts`) |
| `app.routes.ts` | rota `/formularios` com `guardaDono` |
| `layout/shell/shell.html` | link "Formulários do site" |

Tela **própria**, não uma seção a mais em Configurações: ela tem credencial na tela, dois blocos
de código para copiar e um botão destrutivo. Misturar isso com janela de atendimento e feriado
deixaria as duas telas piores — e o `configuracoes.html` já tem 11 KB.

---

## As cinco camadas, e por que nenhuma basta sozinha

Este é o **segundo endpoint do sistema que aceita escrita sem sessão** — o outro é o webhook da
Evolution, que ao menos vem de uma origem só.

1. **Chave por formulário** — identifica o tenant e permite revogar granularmente.
   Sozinha: quem copia o HTML da página do cliente tem a chave.
2. **Rate limit por IP** — política `captura`, janela **fixa** de 1 min, 10 requisições.
   Sozinha: não impede um envio abusivo por minuto, indefinidamente.
3. **Origem permitida** — compara o host do `Origin` com o domínio cadastrado.
   Sozinha: vale zero contra `curl`, que não manda `Origin` — e por isso ela **não recusa** quando
   o cabeçalho está ausente (ver abaixo).
4. **Honeypot** — campo fora da tela; veio preenchido, descarta.
   Sozinha: bot sob medida não cai.
5. **Validação real** — telefone canonicalizado, nome com mínimo, corpo com teto de 2000.
   Sozinha: não é proteção, é higiene.

### Por que a janela do rate limit é fixa e não deslizante

O teto é por minuto corrido e a mensagem de 429 promete "aguarde N segundos". Com janela
deslizante as vagas voltam aos poucos, e o visitante do site do cliente não tem como saber
quando — a promessa da mensagem ficaria falsa.

### Por que 10 e não 3

O formulário fica no site do cliente e visitantes atrás do mesmo NAT corporativo compartilham o
IP. Um teto apertado recusaria lead legítimo, que é o oposto do que o endpoint existe para fazer.
Ninguém preenche formulário dez vezes por minuto; script, sim.

### CORS: política própria, e por que ela é aberta

A política do painel tem lista fixa de origens em produção. Ela barraria o site de **todo cliente
novo** com "No 'Access-Control-Allow-Origin' header is present" — e o dono não teria como
diagnosticar. Sem uma política separada, o snippet gerado só funcionaria se a API e o site
estivessem na mesma origem, o que nunca é o caso.

`AllowAnyOrigin()` na rota de captação **não afrouxa nada**, e a razão importa:

- CORS é controle de **leitura no navegador**. Ele nunca impediu a requisição de chegar ao
  servidor — `curl` o ignora por completo. Fechar aqui só esconderia a resposta de quem já
  escreveu no banco.
- **Não há autoridade ambiente** neste endpoint: sem cookie, sem `Authorization`. A credencial é a
  chave, que vai na URL. Não há CSRF a prevenir — uma página hostil não consegue nada que já não
  conseguisse com um POST direto.
- Quem de fato recusa origem errada é o `ServicoCaptura`, no **servidor**, com 400. Essa checagem
  sobrevive ao CORS estar aberto.

`AllowCredentials` é ilegal junto de `AllowAnyOrigin` — e não há o que permitir aqui. Só `POST`:
nenhum outro verbo existe nesta rota. `Retry-After` fica exposto para o formulário do site
conseguir ler quanto esperar depois de um 429.

**O painel continua protegido:** a mesma origem estrangeira recebe 204 sem cabeçalho CORS nenhum
em `/api/formularios` — verificado (abaixo).

### Por que `Origin` ausente NÃO é recusado

`Origin` é posto pelo navegador e JavaScript da página não o altera — é isso que dá algum valor à
camada 3. Ausente significa que **não há navegador na frente** (curl, servidor, app mobile).
Recusar aí bloquearia integração legítima por servidor sem impedir absolutamente nada de quem
monta a requisição à mão. A camada 3 corta abuso *via navegador*, e só.

---

## Decisões que valem registro

### A resposta é sempre a mesma

`200` com corpo idêntico para lead criado, telefone repetido e honeypot. Se a resposta
distinguisse "criado" de "já existia", o formulário viraria um **verificador de clientes**:
bastaria testar telefones para descobrir quem é cliente de quem. A distinção existe no log e no
retorno do serviço (`ResultadoCaptura`), que é onde ela é útil.

Erro de *validação* devolve 400 — aí o visitante digitou errado e precisa poder corrigir.

Chave inexistente e formulário desativado devolvem **a mesma mensagem**: distinguir contaria a
quem sonda que a chave existe e só está pausada.

### Duplicata é consulta, não exceção

`uq_contatos_telefone` recusaria o insert — e viraria **500 no formulário do site do cliente**.
Erro de banco não é fluxo de controle. A duplicata é uma consulta prévia; o resultado é um
lembrete **manual** para quem já cuida do contato, com o texto que a pessoa escreveu.

Manual e não automático de propósito: automático é do motor de follow-up, tem teto diário e
semântica de robô. Este é recado de pessoa para pessoa.

O predicado repete `anonimizado_em IS NULL` porque `uq_contatos_telefone` é **parcial**. Sem isso
um contato anonimizado bloquearia para sempre a recaptura daquele número.

### O teto diário e por que a guarda é da aplicação

`uq_lembrete_teto_diario` é um índice **parcial**: cobre só
`origem = 'automatico' AND envia_mensagem AND status <> 'cancelado'`.

O lembrete de primeiro contato tem `envia_mensagem = false` — obrigatoriamente, porque a captação
não pode disparar WhatsApp. Logo **o índice não o cobre e o banco não impediria um segundo**.

A guarda é explícita em `CriarLembreteDePrimeiroContatoAsync`: já existe automático não-cancelado
para este contato hoje? Não cria outro, e **não vaza exceção** — retorna em silêncio com log.

### Nenhuma mensagem de WhatsApp

A pessoa deixou o telefone num site; **não iniciou conversa**. Mensagem não solicitada é o caminho
curto para o número ser denunciado — e o Nexora roda em rota não-oficial, onde banimento é risco
real e sem recurso. Só a notificação do painel sai da captação, para o badge subir na hora.

### Tenant zero

Sem sessão, `contexto.EmpresaId` é `0` e o query filter global devolve **vazio em silêncio** — o
lead simplesmente não apareceria, sem erro em lugar nenhum. Toda consulta do `ServicoCaptura` usa
`IgnoreQueryFilters()` **mais** filtro explícito por `empresaId`, que vem da chave.

O `ServicoFormularios` faz o oposto e de propósito: é caminho autenticado, o query filter vale, e
o `FirstOrDefault` nulo vira "não encontrado" tanto para id inexistente quanto para id de outro
tenant — distinguir contaria que o formulário existe em outra empresa.

### O snippet gerado é produto

Ele sai da tela e vai para o site do cliente, onde fica por anos e ninguém o revisa. Por isso:

- a URL sai do `API` do environment, **nunca escrita à mão** — chumbar quebraria em produção sem aviso;
- o campo-armadilha fica **fora da tela por posicionamento**, não por `display:none`: bot decente
  pula campo escondido por display e preenche o que só está posicionado longe. `tabindex="-1"` e
  `aria-hidden` mantêm teclado e leitor de tela fora dele;
- **zero dependências** — sem jQuery, sem biblioteca de captcha. Cola e funciona;
- o `name` do campo-armadilha é `website`, porque é o tipo de nome que atrai bot; o mapeamento
  para `armadilha` acontece no `JSON.stringify`.

### A chave na tela

Fica **mascarada** até alguém clicar em "Mostrar", e volta a esconder quando o painel fecha. Ela
abre um endpoint de escrita na internet — deixá-la impressa na lista é deixá-la em qualquer print
e em qualquer monitor esquecido aberto.

Regerar tem confirmação que **diz o preço** ("o formulário no seu site vai recusar todos os envios
até você colar o HTML novo lá") em vez de perguntar "tem certeza?" — um "tem certeza" genérico é
clicado sem ler.

---

## Testes

**Backend — `tests/Nexora.Tests/Integracao/CapturaDbTests.cs`, 18 casos** (4 do `[Theory]` de
telefone). Os dez exigidos pelo prompt:

| exigido | teste |
|---|---|
| cria contato na etapa de menor ordem, origem `site` | `CAPTURA_CRIA_CONTATO_NA_ETAPA_DE_MENOR_ORDEM_COM_ORIGEM_SITE` |
| chave inválida ou revogada é recusada | `CHAVE_INVALIDA_OU_REVOGADA_E_RECUSADA` (inexistente, desativada e regerada) |
| chave da A nunca escreve na B | `A_CHAVE_DA_EMPRESA_A_NUNCA_ESCREVE_NA_EMPRESA_B` |
| telefone existente gera lembrete, não duplicata | `TELEFONE_JA_EXISTENTE_GERA_LEMBRETE_E_NAO_CONTATO_DUPLICADO` |
| honeypot responde 200 e não cria nada | `HONEYPOT_PREENCHIDO_RESPONDE_SUCESSO_E_NAO_CRIA_NADA` |
| origem não permitida é recusada | `ORIGEM_NAO_PERMITIDA_E_RECUSADA` |
| telefone inválido é recusado | `TELEFONE_INVALIDO_E_RECUSADO` (`[Theory]`, 4 entradas) |
| rate limit barra a 11ª no minuto | `O_ENDPOINT_PUBLICO_TEM_RATE_LIMIT_DE_10_POR_MINUTO` + verificação ao vivo (abaixo) |
| nenhuma mensagem de WhatsApp é disparada | `NENHUMA_MENSAGEM_DE_WHATSAPP_E_DISPARADA_PELA_CAPTURA` e `O_LEAD_GANHA_LEMBRETE...` |
| 2º automático no mesmo dia é barrado sem exceção vazando | `SEGUNDO_LEMBRETE_AUTOMATICO_NO_MESMO_DIA_E_BARRADO_SEM_EXCECAO` |

Mais: `A_CAPTURA_FUNCIONA_COM_O_CONTEXTO_ZERADO` (o caminho real de produção),
`Sem_cabecalho_Origin_a_checagem_de_dominio_nao_barra`, `Nome_curto_e_recusado_e_mensagem_longa_e_cortada`,
`Configurar_formulario_e_so_do_DONO`.

**Frontend — `paginas/formularios/formularios.spec.ts`, 6 casos.** O terceiro **injeta o snippet
numa página de verdade, recria a tag `<script>` (o `innerHTML` não executa script inserido assim),
dispara o submit e intercepta o `fetch`** — ler a string com regex não provaria nada, porque o
snippet pode conter tudo que se procura e ainda assim não enviar.

### Mutação, para não ficar em teste que não morde

Removi `.IgnoreQueryFilters()` da busca da etapa em `ServicoCaptura` e rodei:
`A_CHAVE_DA_EMPRESA_A_NUNCA_ESCREVE_NA_EMPRESA_B` e `A_CAPTURA_FUNCIONA_COM_O_CONTEXTO_ZERADO`
reprovaram, os outros 16 passaram. A proteção de tenant está de fato coberta. Revertido.

### Duas falhas reais que os testes pegaram

1. `expect(html).not.toContain('display:none')` reprovou — o **comentário do próprio snippet** diz
   "não troque por `display:none`", e a busca pela literal acusava o aviso em vez do estilo. A
   assertiva passou a inspecionar o `style` do elemento no DOM parseado.
2. Um tick de microtarefa pegava o `fetch` mas não a resposta: a cadeia do snippet tem dois
   `.then` encadeados. Trocado por uma macrotarefa.

---

## Verificação ao vivo

API em `localhost:5123` contra `nexora_dev`, formulário criado pela própria tela, com domínio
`www.cliente.com.br`:

```
Origem errada  -> 400 {"erro":"Origem não permitida para este formulário."}
Honeypot       -> 200 {"recebido":true,"mensagem":"Recebemos seu contato..."}
Tel invalido   -> 400 {"erro":"Informe um telefone válido com DDD."}
Lead valido    -> 200 {"recebido":true,"mensagem":"Recebemos seu contato..."}
```

Rate limit: as 4 chamadas acima já consomem cota, mais 6 do laço = **10 aceitas; a 11ª devolveu
429**. Exatamente o contrato.

CORS, com a API rodando e chamadas de uma origem estrangeira:

```
PREFLIGHT /api/captura   -> 204  Allow-Origin: *  Allow-Methods: POST  Allow-Headers: content-type
POST      /api/captura   -> 400  Allow-Origin: *  Expose-Headers: Retry-After
PREFLIGHT /api/formularios -> 204  (nenhum cabeçalho CORS — o painel segue barrado)
```

E a resposta de 429 também carrega os cabeçalhos, com `Retry-After: 60` exposto — sem isso o
navegador bloquearia a leitura e o visitante veria "erro de conexão" no lugar de "aguarde 60
segundos":

```
429 na 11ª tentativa
  Access-Control-Allow-Origin: * | Access-Control-Expose-Headers: Retry-After | Retry-After: 60
  {"erro":"Muitas tentativas. Aguarde 60 segundos e tente novamente."}
```

Estado no banco depois:

- 7 leads contados em `leads_recebidos` — **o honeypot não incrementou** e os dois 400 também não;
- contatos com `origem = 'site'`, `origem_detalhe` = nome do formulário, `responsavel_id` nulo,
  todos na etapa de `ordem = 1`;
- 7 lembretes `origem = automatico`, `envia_mensagem = false`, `texto_mensagem` nulo;
- **zero mensagens de saída**.

Os dados de teste foram apagados do `nexora_dev` em seguida.

**Build e testes:** `dotnet build -warnaserror` limpo, `ng build` limpo,
**424 testes de backend** e **105 de frontend** verdes.

---

## Pendências

1. **O rate limit continua em memória.** Vale para uma instância só. Duas instâncias atrás de um
   balanceador dobram o teto efetivo — e este agora é um endpoint público na internet, então o
   custo de esquecer isso subiu. Já registrado em `PROGRESSO.md`; a captação não piora o problema,
   mas aumenta o que ele protege.

2. **`ConfiarProxyReverso` precisa ser ligado em produção.** Com ele desligado atrás de
   Cloudflare/nginx, *todos* os visitantes compartilham o IP do proxy e o teto de 10/min viraria um
   teto global — o formulário pararia de receber lead depois da décima submissão do site inteiro.
   É a configuração mais provável de quebrar a captação em produção.

3. **Domínio permitido aceita um host só.** Quem tem `cliente.com.br` e `www.cliente.com.br`
   precisa de dois formulários ou de deixar o campo em branco. Uma lista separada por vírgula
   resolveria; ficou de fora para não inventar formato antes de alguém precisar.

4. **Sem tela de "leads recebidos por formulário"** além do contador. Rastrear de onde veio cada
   lead já é possível pelo `origem_detalhe` do contato, mas não há filtro por formulário na lista
   de contatos.

5. **O contador `leads_recebidos` conta submissões aceitas, não contatos criados.** Duplicata
   conta; honeypot e erro de validação não. O rótulo da tela diz "leads", que é próximo o bastante,
   mas não é a mesma coisa que contatos novos.
