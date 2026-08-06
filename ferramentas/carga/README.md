# Carga de dados pelos controllers

Popula os tenants de desenvolvimento chamando os **endpoints de verdade**, não escrevendo no
banco. Todo registro passa pela validação, pela regra de negócio e pelo query filter de tenant —
é o que torna a carga uma prova de que o produto funciona, e não só de que o gerador funciona.

Isto é diferente do `ServicoSeedDemonstracao`, que escreve direto pelo EF e existe para montar o
tenant de demonstração em segundos. Os dois têm lugar: o seed é rápido e determinístico, a carga
é lenta e exercita a API.

## Como rodar

```powershell
# 1. A API precisa estar no ar em Development, na 5123.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project src/Nexora.Api --no-launch-profile

# 2. A carga. QUANTOS é o número de contatos POR TENANT (padrão 400).
$env:QUANTOS = '500'
./ferramentas/carga/carga-api.ps1

# 3. Espalhar pelos últimos 120 dias (ver "o que a API não consegue fazer", abaixo).
psql -U postgres -d nexora_dev -f ferramentas/carga/espalhar-datas.sql
psql -U postgres -d nexora_dev -f ferramentas/carga/ajustar-mes-corrente.sql

# 4. Conferir.
./ferramentas/carga/conferir.ps1
```

Edite os e-mails e senhas no fim do `carga-api.ps1` para apontar aos seus tenants.

## O caminho de cada contato

| passo | endpoint |
|---|---|
| lead | `POST /api/contatos` |
| mensagem recebida | `POST /api/webhook/evolution` (`messages.upsert`) |
| mensagem enviada | `POST /api/webhook/evolution` (`messages.upsert` com `fromMe: true`) |
| anda no funil | `POST /api/funil/{id}/mover` |
| venda / perda | `POST /api/contatos/{id}/ganho` · `/perda` |
| agenda | `POST /api/lembretes` |

### Por que a saída não usa `POST /conversas/{id}/responder`

Aquele endpoint chama `InstanciaConectadaAsync`, que **pergunta à Evolution ao vivo**. Sem uma
instância real rodando ele devolve 409 — e está certo: aceitar a resposta para depois empilhar
erro seria pior para quem está olhando a tela.

O caminho usado é o outro caminho real de mensagem de saída: `messages.upsert` com `fromMe: true`,
que é como a Evolution avisa que a pessoa respondeu **pelo celular** em vez de pelo painel. Mesmo
webhook, mesmo processador, mesma tabela.

## Três coisas que a API não consegue fazer, e por quê

**1. Data no passado.** O `InterceptorAuditoria` carimba `criado_em` em todo INSERT — de
propósito, para nenhum caminho de escrita esquecer a coluna. Pela API tudo nasce hoje, e o
dashboard vira um pico único. Daí o `espalhar-datas.sql`, que desloca cada contato **e toda a
subárvore dele** (conversas e mensagens) pelo mesmo intervalo — deslocar linha a linha quebraria
`ultima_mensagem_em` e `aguardando_desde`.

**2. Conversão crível.** O dashboard mede ganhos ÷ (ganhos + perdas) do **mês corrente**.
Espalhados uniformemente em 120 dias, o mês corrente fica com poucos desfechos e a conversão sai
100% ou 0%. O `ajustar-mes-corrente.sql` traz uma fatia para o mês — mexendo só em `ganho_em` e
`perdido_em`, nunca em `criado_em`: negócio criado há três meses e fechado esta semana é o caso
mais comum de um funil de verdade.

**3. Rate limit.** `GeralPorMinuto` é 100 em produção, calibrado para uso humano. Milhares de
requisições em minutos batem nele na primeira dezena. `appsettings.Development.json` sobe o teto,
e só em Development. Login e recuperação de senha ficam de fora de propósito: são as defesas
contra força bruta e enumeração de contas, e afrouxá-las em dev ensinaria a equipe a nunca
esbarrar nelas.

## Segurança

**Todos os telefones ficam na faixa reservada `5500`** (DDD 00, que não existe no plano nacional),
inclusive nos tenants que **não** estão marcados como demonstração. É a única barreira que vale
independente da flag da empresa — o `EnviadorMensagem` recusa número de demonstração em qualquer
tenant. Nenhum contato criado aqui pode receber mensagem de verdade.

A carga também abre a conexão via `connection.update`. Isso **não** faz nada ser disparado: não há
Evolution por trás, e a barreira do número vale de qualquer forma.

## Efeito colateral a saber

Tenant **não** marcado como demonstração entra na rodada do `MotorFollowUp`. Com centenas de
contatos e conversas paradas, a rodada vai processá-los e gravar uma linha de mensagem falhada
por contato elegível — todas recusadas pela faixa de telefone, mas ainda assim linhas.

Se isso incomodar, marque o tenant como demonstração (`empresas.demonstracao = true`) — ao custo
de bloquear qualquer envio real a partir dele.
