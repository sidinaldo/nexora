# =============================================================================================
# CARGA DE DADOS PELOS CONTROLLERS.
#
# Nada aqui escreve no banco. Todo registro nasce de uma chamada HTTP ao endpoint de verdade,
# passando pela validacao, pela regra de negocio e pelo query filter de tenant -- que e o que
# torna esta carga uma PROVA de que o produto funciona, e nao so de que o gerador funciona.
#
# O caminho de cada contato:
#   POST /api/contatos            -> cria o lead
#   POST /api/webhook/evolution   -> mensagem RECEBIDA (o caminho real de entrada; cria a conversa)
#   POST /api/conversas/{id}/responder -> resposta do vendedor
#   POST /api/funil/{id}/mover    -> anda no funil
#   POST /api/contatos/{id}/ganho -> venda    |  /perda -> perdido
#   POST /api/lembretes           -> agenda do Meu Dia
#
# TELEFONE: faixa reservada 5500 (DDD 00, que nao existe no plano nacional) para os DOIS
# tenants -- inclusive o que nao esta marcado como demonstracao. E a unica barreira que vale
# independente da flag da empresa, entao nenhum contato falso pode receber mensagem de verdade.
# =============================================================================================
Add-Type -AssemblyName System.Net.Http
$ErrorActionPreference = 'Stop'

$BASE = 'http://localhost:5123/api'
$SEGREDO_WEBHOOK = 'segredo-de-webhook-para-teste-local'

$http = New-Object System.Net.Http.HttpClient
$http.Timeout = [TimeSpan]::FromMinutes(3)

$script:token = $null
$script:erros = @{}
$script:chamadas = 0

function Chamar($metodo, $rota, $json, [switch]$semToken) {
    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($metodo), "$BASE$rota")
    if ($json) { $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json') }
    if ($script:token -and -not $semToken) {
        $req.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $script:token)
    }
    $resp = $http.SendAsync($req).Result
    $script:chamadas++
    $corpo = $resp.Content.ReadAsStringAsync().Result
    $status = [int]$resp.StatusCode

    if ($status -ge 400) {
        $chave = "$metodo $rota -> $status"
        if (-not $script:erros.ContainsKey($chave)) { $script:erros[$chave] = $corpo }
    }
    [pscustomobject]@{ Status = $status; Corpo = $corpo }
}

function Json($obj) { $obj | ConvertTo-Json -Compress -Depth 6 }

# ---- vocabulario, o mesmo tom do seed: gente escrevendo no WhatsApp ----
$PRIMEIROS = @('Marcos','Juliana','Rafael','Camila','Diego','Patricia','Bruno','Leticia','Gustavo',
  'Renata','Fabio','Simone','Andre','Vanessa','Leonardo','Tatiane','Rodrigo','Cristina','Eduardo',
  'Larissa','Marcio','Adriana','Thiago','Beatriz','Otavio','Sandra','Vinicius','Carla','Henrique','Monica')
$SOBRENOMES = @('Albuquerque','Bandeira','Cavalcanti','Domingues','Esteves','Ferraz','Guimaraes',
  'Lacerda','Maranhao','Novaes','Oliveira','Pontes','Queiroz','Ramalho','Siqueira','Tavares',
  'Valadares','Wanderley','Xavier','Zanetti','Almeida','Braga','Coutinho','Medeiros','Portela')
$ENTRADAS = @('oi, boa tarde! vcs atendem sabado?','quanto fica o orcamento?','consigo agendar pra quinta?',
  'ainda tem vaga essa semana?','bom dia! vi o anuncio de voces','vcs parcelam no cartao?','qual o endereco?',
  'obrigado! vou pensar e retorno','pode ser de manha?','e se eu levar duas, tem desconto?','fechado, pode marcar')
$SAIDAS = @('Oi! Tudo bem? Atendemos sim, das 8h as 14h no sabado.','Claro, consigo fazer o orcamento hoje ainda.',
  'Quinta as 14h esta livre. Reservo pra voce?','Temos vaga sim! Prefere manha ou tarde?',
  'Parcelamos em ate 6x sem juros.','Ficamos na Av. das Palmeiras, 1200.','Sem problema! Qualquer duvida e so chamar.')
$ORIGENS = @('instagram','instagram','instagram','whatsapp','whatsapp','indicacao','indicacao',
  'google','site','facebook','qrcode','manual','outro')
$MOTIVOS = @('Achou mais barato em outro lugar','Desistiu do servico','Sumiu depois do orcamento',
  'Comprou com concorrente','Fora da nossa regiao de atendimento')
$TITULOS = @('Ligar para confirmar o horario','Mandar o orcamento revisado','Retomar contato',
  'Confirmar a entrega','Perguntar se ficou tudo certo')

# =============================================================================================
function Carregar($email, $senha, $quantos, $faixaTelefone) {
    Write-Host "`n=========================================================="
    Write-Host " $email  -- $quantos contatos"
    Write-Host "=========================================================="

    $login = Chamar POST '/auth/login' (Json @{ email = $email; senha = $senha }) -semToken
    if ($login.Status -ne 200) { Write-Host "  LOGIN FALHOU: $($login.Corpo)"; return $null }
    $script:token = ($login.Corpo | ConvertFrom-Json).token
    $empresa = ($login.Corpo | ConvertFrom-Json).usuario.empresaNome
    Write-Host "  entrou em: $empresa"

    $etapas = @((Chamar GET '/etapas' $null).Corpo | ConvertFrom-Json) | Sort-Object ordem
    $abertas = @($etapas | Where-Object { -not $_.eGanho })
    $ganho = $etapas | Where-Object { $_.eGanho }
    $equipe = @((Chamar GET '/equipe' $null).Corpo | ConvertFrom-Json) | Where-Object { $_.status -eq 'ativo' }
    $conexao = (Chamar GET '/conexao' $null).Corpo | ConvertFrom-Json
    $instancia = $conexao.instanceName
    Write-Host "  etapas: $($etapas.Count) ($($abertas.Count) abertas)  equipe: $($equipe.Count)  instancia: $instancia"

    # ---- conexao ABERTA, pelo webhook ----
    # Sem isto, `POST /conversas/{id}/responder` devolve 409 "WhatsApp desconectado" e a base
    # fica so com mensagens de ENTRADA -- caixa inteira em vermelho, nenhuma conversa respondida.
    #
    # E o mesmo evento `connection.update` que a Evolution manda de verdade quando o QR e lido:
    # nao ha UPDATE no banco aqui, e o status passa pelo mesmo processador de sempre.
    #
    # Nao ha Evolution rodando por tras. Isso NAO gera disparo para ninguem: os telefones estao
    # na faixa 5500, e o `EnviadorMensagem` recusa numero de demonstracao em QUALQUER tenant --
    # a linha da mensagem fica gravada com o motivo, que e o protocolo normal de falha.
    $abrir = @"
{"event":"connection.update","instance":"$instancia","data":{"state":"open"}}
"@
    $null = Chamar POST "/webhook/evolution?token=$SEGREDO_WEBHOOK" $abrir -semToken
    $conexao = (Chamar GET '/conexao' $null).Corpo | ConvertFrom-Json
    Write-Host "  conexao: $($conexao.status)"

    $rnd = New-Object System.Random(20260806)
    $criados = 0; $comConversa = 0; $msgs = 0; $vendas = 0; $perdas = 0; $lembretes = 0
    $inicio = Get-Date

    for ($i = 1; $i -le $quantos; $i++) {
        $telefone = '{0}9{1:D8}' -f $faixaTelefone, $i
        $nome = '{0} {1}' -f $PRIMEIROS[$i % $PRIMEIROS.Count], $SOBRENOMES[[math]::Floor($i / $PRIMEIROS.Count) % $SOBRENOMES.Count]

        # ---- 1. o lead, pelo endpoint de contatos ----
        $corpo = @{
            nome     = $nome
            telefone = $telefone
            origem   = $ORIGENS[$i % $ORIGENS.Count]
        }
        if ($i % 4 -eq 0) { $corpo.email = "contato$i@exemplo.com.br" }
        if ($i % 3 -eq 0) { $corpo.valor = [math]::Round($rnd.Next(300, 9000), 2) }
        if ($equipe.Count -gt 0 -and $i % 5 -ne 0) { $corpo.responsavelId = $equipe[$i % $equipe.Count].id }

        $r = Chamar POST '/contatos' (Json $corpo)
        if ($r.Status -ne 200) { continue }
        $contatoId = ($r.Corpo | ConvertFrom-Json).id
        $criados++

        # ---- 2. conversa: 2 de cada 3 contatos escrevem, pelo WEBHOOK ----
        if ($i % 3 -ne 0) {
            $quantasEntradas = 1 + ($i % 3)
            for ($m = 0; $m -lt $quantasEntradas; $m++) {
                $texto = $ENTRADAS[($i + $m) % $ENTRADAS.Count]
                $payload = @"
{"event":"messages.upsert","instance":"$instancia","data":{"key":{"id":"CARGA-$i-$m","remoteJid":"$telefone@s.whatsapp.net","fromMe":false},"pushName":"$nome","messageType":"conversation","message":{"conversation":"$texto"},"messageTimestamp":$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())}}
"@
                $w = Chamar POST "/webhook/evolution?token=$SEGREDO_WEBHOOK" $payload -semToken
                if ($w.Status -eq 200) { $msgs++ }
            }
            $comConversa++

            # ---- 3. a resposta da empresa ----
            #
            # NAO por POST /conversas/{id}/responder. Aquele endpoint chama
            # `InstanciaConectadaAsync`, que pergunta a EVOLUTION ao vivo -- e nao ha Evolution
            # rodando aqui. O produto esta certo em recusar: aceitar a resposta e so empilhar
            # erro depois seria pior para o vendedor que esta olhando a tela.
            #
            # O caminho usado e o outro caminho REAL de mensagem de saida: `messages.upsert` com
            # `fromMe: true`, que e como a Evolution avisa que a pessoa respondeu pelo CELULAR em
            # vez de pelo painel. Mesmo webhook, mesmo processador, mesma tabela.
            #
            # A maioria das conversas fica RESPONDIDA. Deixar todas esperando pintaria a caixa
            # inteira de vermelho -- tecnicamente correto e pessimo como retrato de operacao.
            if ($i % 4 -ne 0) {
                $texto = $SAIDAS[$i % $SAIDAS.Count]
                $saida = @"
{"event":"messages.upsert","instance":"$instancia","data":{"key":{"id":"CARGA-OUT-$i","remoteJid":"$telefone@s.whatsapp.net","fromMe":true},"messageType":"conversation","message":{"conversation":"$texto"},"messageTimestamp":$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())}}
"@
                $w = Chamar POST "/webhook/evolution?token=$SEGREDO_WEBHOOK" $saida -semToken
                if ($w.Status -eq 200) { $msgs++ }
            }
        }

        # ---- 4. destino do lead: venda, perda ou segue no funil ----
        $sorte = $i % 10
        if ($sorte -lt 2) {
            $r = Chamar POST "/contatos/$contatoId/ganho" (Json @{ valor = [math]::Round($rnd.Next(450, 9000), 2) })
            if ($r.Status -lt 300) { $vendas++ }
        }
        elseif ($sorte -lt 3) {
            $r = Chamar POST "/contatos/$contatoId/perda" (Json @{ motivo = $MOTIVOS[$i % $MOTIVOS.Count] })
            if ($r.Status -lt 300) { $perdas++ }
        }
        elseif ($abertas.Count -gt 1) {
            # Anda no funil pelo endpoint de mover -- que e o mesmo que o arrastar do kanban usa.
            # A etapa e sorteada com peso decrescente, para o funil AFUNILAR em vez de virar
            # um retangulo.
            $peso = $rnd.NextDouble()
            $alvo = if ($peso -lt 0.45) { 0 } elseif ($peso -lt 0.72) { 1 }
                    elseif ($peso -lt 0.89) { [math]::Min(2, $abertas.Count - 1) }
                    else { $abertas.Count - 1 }
            if ($alvo -gt 0) {
                $null = Chamar POST "/funil/$contatoId/mover" (Json @{ etapaId = $abertas[$alvo].id })
            }
        }

        # ---- 5. agenda do Meu Dia: poucos, e absolutos ----
        # Uma tela com centenas de pendencias nao e agenda, e lista que ninguem abre.
        if ($i -le 24 -and $sorte -ge 3) {
            $dataAlvo = if ($i % 3 -eq 0) { (Get-Date).ToString('yyyy-MM-dd') }
                        else { (Get-Date).AddDays(-$rnd.Next(1, 6)).ToString('yyyy-MM-dd') }
            $r = Chamar POST '/lembretes' (Json @{
                contatoId = $contatoId
                dataAlvo  = $dataAlvo
                titulo    = $TITULOS[$i % $TITULOS.Count]
                observacao = 'Gerado na carga pela API.'
            })
            if ($r.Status -lt 300) { $lembretes++ }
        }

        if ($i % 50 -eq 0) {
            $seg = [math]::Round(((Get-Date) - $inicio).TotalSeconds)
            Write-Host ("  {0,5}/{1}  contatos={2} conversas={3} msgs={4} vendas={5} perdas={6}  {7}s" -f `
                $i, $quantos, $criados, $comConversa, $msgs, $vendas, $perdas, $seg)
        }
    }

    $seg = [math]::Round(((Get-Date) - $inicio).TotalSeconds, 1)
    Write-Host ("  FIM: {0} contatos, {1} conversas, {2} mensagens, {3} vendas, {4} perdas, {5} lembretes em {6}s" -f `
        $criados, $comConversa, $msgs, $vendas, $perdas, $lembretes, $seg)

    [pscustomobject]@{ Empresa = $empresa; Contatos = $criados; Conversas = $comConversa
                       Mensagens = $msgs; Vendas = $vendas; Perdas = $perdas; Lembretes = $lembretes }
}

# =============================================================================================
$quantos = if ($env:QUANTOS) { [int]$env:QUANTOS } else { 400 }

$r1 = Carregar 'ana@padaria.com'            'padaria-dev-2026'   $quantos '5500'
$r2 = Carregar 'ana.demo@nexora.exemplo'    'demonstracao-2026'  $quantos '5500'

Write-Host "`n=========================================================="
Write-Host " TOTAL DE CHAMADAS HTTP: $($script:chamadas)"
if ($script:erros.Count -gt 0) {
    Write-Host " ERROS DISTINTOS:"
    $script:erros.GetEnumerator() | ForEach-Object { Write-Host "   $($_.Key)"; Write-Host "     $($_.Value)" }
} else { Write-Host " Nenhum erro." }
