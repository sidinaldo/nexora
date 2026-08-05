# Bloco 11 — Envio de e-mail transacional

Estado: **fechado**. Os 4 critérios verificados por execução.

`dotnet build` e `ng build` limpos, sem warning. **287 testes verdes** (270 antes → 17 novos).

---

## 1. O provedor escolhido, e por quê

**SMTP genérico.** Nenhum SDK de fornecedor.

| Critério | Por que SMTP venceu |
|---|---|
| Portabilidade | O mesmo código fala com Resend, Amazon SES, Brevo, Mailgun, Zoho e com o servidor do próprio cliente. Trocar de fornecedor é trocar host, usuário e senha no user-secrets |
| Dependências | Zero. Um SDK traria acoplamento a um fornecedor em troca de webhooks de entrega e estatísticas, que a fase 1 não usa |
| Reversibilidade | Quando esses recursos fizerem falta, é outra implementação de `IRemetenteEmail` e uma linha de DI — nenhum serviço de domínio muda |

**Cliente: `System.Net.Mail.SmtpClient` da BCL.** A documentação da Microsoft recomenda MailKit
para desenvolvimento novo, e mesmo assim fiquei com a BCL. O motivo é o escopo: uma tentativa,
sem retry, sem OAuth, contra um relay que fala STARTTLS na 587. É exatamente o que a BCL faz bem.

**O limite real, registrado:** o `SmtpClient` da BCL **não fala SMTPS implícito (porta 465)** —
`EnableSsl` nele significa STARTTLS. Provedor que só ofereça 465 não funciona, e a saída é
MailKit. Também não honra `CancellationToken` no envio; quem corta é o `Timeout` (15s por padrão).

### Em desenvolvimento

`Email:Provedor = "arquivo"` grava `.html` e `.txt` em `emails-dev/` em vez de enviar. É **outra
implementação da mesma interface**, escolhida no DI — não um `if` no meio do notificador. A
diferença importa: com `if`, o caminho de produção nunca roda em dev e o de dev vira código morto
em produção; os dois divergem e ninguém percebe até o deploy.

`arquivo` é o **padrão** porque um clone limpo tem que subir e funcionar sem ninguém configurar
SMTP — e porque errar para o lado de não enviar é sempre mais barato que errar para o lado de
enviar.

---

## 2. O que foi criado

### Core (sem I/O, sem provedor)

| Arquivo | O que é |
|---|---|
| `Email/IRemetenteEmail.cs` | O transporte. Uma tentativa; lança se não entregar |
| `Email/INotificadorEmail.cs` | Os três e-mails. **Nenhum método lança** |
| `Email/MontadorEmail.cs` | Os templates. Função pura — testável sem SMTP |
| `Entidades/EmailEnviado.cs` | O registro de tentativas |

### Infra

| Arquivo | O que é |
|---|---|
| `Email/OpcoesEmail.cs` | Seção `Email` do appsettings |
| `Email/RemetenteSmtp.cs` | Entrega por SMTP |
| `Email/RemetenteArquivo.cs` | Grava em disco (desenvolvimento) |
| `Email/NotificadorEmail.cs` | Monta → entrega → registra |
| Migration `RegistroDeEmail` | Tabela `emails_enviados` |

### Api e frontend

| Arquivo | O que é |
|---|---|
| `RedefinicaoController` | `POST /api/redefinir/solicitar` — "esqueci minha senha" |
| `RateLimitingConfig` | Política `recuperacao`: 3 por 15 min, **por IP** |
| `paginas/esqueci/` | A tela pública |
| `login.html` | Link "Esqueci minha senha" |

---

## 3. Envio confiável — as duas decisões

### 3.1 Fora da transação principal

O `SaveChanges` do convite acontece **antes** do envio. Se o provedor estiver fora, o usuário já
existe, o token já está no banco, e a API já vai devolver o token para a tela.

**O fallback manual não sai do produto.** O dono continua vendo o link e podendo copiá-lo — que
era o único caminho antes deste bloco.

O contrato do `INotificadorEmail` é explícito: **nenhum método lança**. Deixar a exceção subir
faria o usuário não ser criado porque o e-mail falhou — a dependência menos confiável do sistema
derrubando a operação mais importante dele.

O preço é a falha ser silenciosa para quem chamou. Daí a segunda decisão.

### 3.2 Registro de tudo

`emails_enviados`: destinatário, tipo, assunto, quando, sucesso e o erro. Gravado nos **dois**
caminhos.

Sem isso, "o cliente diz que não recebeu" é indepurável — não dá para saber se o sistema tentou,
se o provedor recusou, se o endereço estava errado ou se caiu no spam. Com isso, as três
primeiras hipóteses se respondem numa consulta.

**Não é fila.** Não há coluna de tentativas nem de próxima execução, e nada relê a tabela para
reenviar. Reenvio é o dono clicando de novo. Fila com retry e backoff é fase 2, se o volume
justificar.

Nem o registro pode derrubar a operação: o `INSERT` tem seu próprio `try/catch`. Se o banco
recusar a linha, o convite já foi criado e o e-mail já foi (ou não) entregue — abortar agora
desfaria trabalho útil por causa de um log.

O índice é `(destinatario, enviado_em DESC)`: depurar "não recebi" começa sempre pelo
destinatário, nunca pela empresa — e no reset público nem há empresa.

---

## 4. "Esqueci minha senha" — a disciplina da resposta única

O método do serviço devolve `Task`, não `Task<bool>`. **Não há o que diferir.** E-mail
inexistente é no-op silencioso.

O controller responde **200 com o mesmo corpo** em qualquer caso:

> "Se houver uma conta com esse e-mail, enviamos um link para redefinir a senha. Confira também a
> caixa de spam."

O texto é redigido para ser **verdadeiro nos dois casos**: não afirma que enviou. Dizer "enviamos"
quando a conta não existe seria mentira; dizer "não existe" entregaria a lista de clientes a quem
estiver testando endereços — e essa lista tem valor para quem monta phishing. É a mesma disciplina
do login com `HashDummy` (`PoliticaLogin`, bloco 1).

A tela segue a mesma regra: não existe caminho de erro "e-mail não encontrado".

**Só usuário ATIVO recebe.** Quem ainda não aceitou o convite não tem senha para redefinir, e o
caminho dele é o reenvio do convite.

### Rate limit

Política nova, `recuperacao`: **3 por 15 minutos, por IP**.

A chave é só o IP, **de propósito**. Com o e-mail do corpo na chave, cada endereço teria seu
próprio balde e um script pediria reset para milhares de endereços sem nunca estourar o limite. Por
IP, o flood para no terceiro — e o domínio remetente, que é quem paga a reputação, fica protegido.

**Sobre timing:** o caminho "existe" faz mais trabalho (grava token, chama SMTP) que o caminho
"não existe", então o tempo de resposta difere. Com 3 medições a cada 15 minutos por IP, isso não
sustenta enumeração. Eliminar a diferença exigiria tirar o envio da requisição, o que significa
um despachante em segundo plano — e isso é fila, que o prompt exclui. Registrado como limite
conhecido em §8.

---

## 5. Os templates

Três: **convite** (7 dias), **reset** (2h) e **senha alterada** (sem link).

HTML escrito para cliente de e-mail, não para browser:

| Regra | Por quê |
|---|---|
| **Tabela** para layout, não flexbox | Outlook renderiza com o motor do Word |
| **CSS inline** em cada elemento | Gmail remove `<style>` do `<head>` |
| **Largura fixa** de 600px | Media query é ignorada por vários clientes |
| **Nenhuma imagem** — o logo é texto | Imagem remota é bloqueada por padrão, e cabeçalho quebrado passa cara de golpe |
| **Texto puro junto** | Cliente que bloqueia HTML e leitor de tela; e só-HTML aumenta a chance de spam |

No SMTP, o **corpo é o texto** e o HTML entra como vista alternativa — a ordem que
`multipart/alternative` espera. Ao contrário, o cliente sem HTML mostraria a marcação crua.

Nome de pessoa e de empresa são **escapados**: vêm de entrada do usuário e entram no corpo.

**O aviso de senha alterada não tem link nenhum.** Um "não fui eu, clique aqui" seria justamente
o vetor de phishing que o aviso existe para combater — ele orienta a procurar quem administra a
conta. Dispara na troca pela tela **e** na redefinição por link (que é o caminho que um invasor
com acesso à caixa de e-mail usaria). **Não** dispara no aceite do convite: é a primeira senha,
não uma troca, e seria ruído no primeiro contato com o produto.

Há teste varrendo os três templates contra 12 palavras de cobrança.

---

## 6. Como cada critério foi verificado

| # | Critério | Resultado |
|---|---|---|
| 1 | `dotnet build` limpo, `dotnet test` verde | 287 testes, zero warning |
| 2 | Os 6 testes listados | Todos, mais 11 outros |
| 3 | Teste manual ponta a ponta | Executado |
| 4 | Nenhuma credencial versionada | Verificado |

### Os testes do critério 2

| Pedido | Teste |
|---|---|
| remetente falso registra a chamada; a aplicação não conhece o provedor | `O_SERVICO_DE_APLICACAO_NAO_CONHECE_O_PROVEDOR` |
| falha do provedor não impede a criação nem invalida o convite | `FALHA_DO_PROVEDOR_NAO_IMPEDE_A_CRIACAO_DO_USUARIO_NEM_INVALIDA_O_CONVITE` |
| toda tentativa é registrada | `TODA_TENTATIVA_E_REGISTRADA_COM_SUCESSO_OU_COM_ERRO` |
| "esqueci" responde igual | `ESQUECI_MINHA_SENHA_RESPONDE_IGUAL_PARA_EMAIL_EXISTENTE_E_INEXISTENTE` |
| rate limit barra a quarta | verificado sobre HTTP (abaixo) |
| token expirado recusado | `TOKEN_EXPIRADO_E_RECUSADO` |

Mais: falha ao gravar o registro também não derruba a operação; o link usa a base configurada e
não uma URL chumbada; o HTML é de e-mail e não de browser; nome com marcação é escapado; o aviso
de senha não tem link; nenhum vocabulário de cobrança; convidado pendente não recebe reset; o
aceite não dispara aviso de troca; o remetente de arquivo grava os dois formatos.

### Critério 3 — o teste manual

Com a API de pé e o remetente de arquivo, o fluxo inteiro. **O link foi extraído do próprio
e-mail**, não do retorno da API — senão o teste não provaria que o e-mail leva a algum lugar:

```
1.  dono convida                        -> token devolvido (fallback da tela)
2.  e-mail gravado em disco             -> .html e .txt
                                           traz o nome da empresa
                                           tabela, sem flex, sem <img>
3.  link EXTRAÍDO do texto puro         -> http://localhost:4200/convite/eeed8496…
                                           e é o mesmo token do banco
4.  GET /convite/{token}                -> "Convidado Bloco 11 / Padaria do Bairro"
5.  aceite define a senha               -> devolve JWT, papel vendedor
6.  POST /auth/login com a senha nova   -> autentica
7.  "esqueci" com conta existente
    e com conta inexistente             -> MESMA mensagem
8.  3ª tentativa                        -> HTTP 200
    4ª tentativa                        -> HTTP 429
9.  e-mail de reset gravado
10. registro no banco:
      convite | convidado.bloco11@exemplo.com | sucesso
      reset   | convidado.bloco11@exemplo.com | sucesso
```

O `nexora_dev` foi limpo depois (usuário de teste removido, registro zerado, pasta apagada).

### Critério 4 — credenciais

`appsettings.json` ganhou a seção `Email` **sem** `Usuario` e `Senha` — só provedor, remetente,
base do painel, porta e timeout. Varredura por `Senha|Usuario|Password` nos `appsettings*.json`
não retorna nada.

O `.env.example` documenta os comandos de user-secrets, com a nota de que usuário e senha nunca
entram no arquivo versionado.

**A base do painel também não é chumbada** (`Email:BaseUrlPainel`): o link do convite tem que
apontar para o domínio de quem hospeda, não para localhost. Há teste.

---

## 7. Decisões próprias

1. **`arquivo` como provedor padrão**, não `smtp`. Clone limpo sobe e funciona; e não enviar por
   engano é mais barato que enviar por engano.
2. **Rate limit por IP apenas**, sem o e-mail na chave (justificado em §4).
3. **O aviso de senha alterada dispara também na redefinição por link.** É o caminho de um
   invasor com acesso à caixa de e-mail, e é onde o aviso mais vale.
4. **O aceite de convite NÃO dispara o aviso.** Primeira senha não é troca.
5. **A hora do aviso vai no fuso de Brasília**, não em UTC. "Alterada às 14:32" permite
   reconhecer a própria ação; "17:32Z" não.
6. **O erro do provedor é cortado em 500 caracteres.** A coluna é para diagnóstico humano; stack
   trace de SMTP não ajuda a responder "por que não chegou".
7. **`emails_enviados` tem `empresa_id` anulável**, como `feriados` — o reset público roda sem
   tenant. Query filter no mesmo formato, admitindo as linhas sem dono.
8. **O e-mail vai mascarado para o log** (`PoliticaLogin.MascararEmail`), mas **inteiro** na
   tabela: lá é dado operacional, sem ele não dá para responder "para onde foi".

---

## 8. Pendências

### Deste bloco

| Limite | Consequência |
|---|---|
| **Uma tentativa, sem retry** | Provedor fora = e-mail perdido. O link continua na tela (convite) ou o usuário pede de novo (reset). Por desenho |
| **Timing do "esqueci" difere** | O caminho "existe" faz mais trabalho. Mitigado pelo limite de 3/15min por IP; eliminar exige despachante em segundo plano |
| **`SmtpClient` não fala porta 465** | Provedor só-SMTPS não funciona. A saída é MailKit — uma classe |
| **Sem bounce e sem webhook de entrega** | "Entregue" aqui significa "o provedor aceitou", não "chegou na caixa" |
| **Sem tela de auditoria de e-mails** | A tabela existe; consultar é SQL |
| **Sem verificação de e-mail no cadastro** | Trocar o e-mail em Minha Conta não pede confirmação no endereço novo |
| **Sem DKIM/SPF documentados** | Configuração de DNS do domínio remetente, fora do código — mas sem isso os e-mails vão para spam |
| Não renderizado em cliente real | Os templates seguem as regras e há teste da marcação; não abri em Outlook/Gmail |

### Carregadas dos blocos anteriores

- **Nenhum telefone pareado** (desde o bloco 3) — segue sendo o único item com risco em vez de
  volume.
- **`ServicoCadastroEmpresa` sem controller** — não há como criar a primeira empresa pela API.
- **Arrasto do kanban não testado em navegador** (bloco 8).
- **Sem endpoint de série temporal** — o gráfico do dashboard só roda no modo demonstração.
- **Sem lock distribuído** no agendador de follow-up.
- **Nenhum teste de frontend.**
- `senhas-dev.sql` na raiz, com senha em texto puro.

---

## 9. O que falta para a fase 1 estar vendável

O autoatendimento deixou de depender de alguém copiar link à mão. Restam:

1. **Parear um telefone e mandar mensagem de verdade** — pendente desde o bloco 3, e o único item
   com risco em vez de volume.
2. **Onboarding**: expor o `ServicoCadastroEmpresa` para a primeira empresa nascer pela tela. É o
   que fecha o ciclo — com ele e com este bloco, alguém consegue virar cliente sem intervenção.
3. **DNS do domínio remetente** (SPF, DKIM, DMARC) — sem isso, os e-mails deste bloco chegam no
   spam.
4. **Testar o arrasto do kanban num navegador.**
