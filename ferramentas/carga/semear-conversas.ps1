# Reescreve a THREAD das conversas mais recentes de cada tenant com dialogos de verdade.
#
# ===================== O QUE ELE NAO FAZ =====================
# Nao cria conversa, nao cria contato e NAO mexe em `ultima_mensagem_em` nem `aguardando_desde`.
# A distribuicao do semaforo que a semeadura geral montou fica intacta - este script so troca as
# mensagens por um dialogo que faz sentido.
#
# IDEMPOTENTE: rodar de novo da exatamente a mesma thread.
# =============================================================
#
# So funciona em Development: a rota /api/dev/* devolve 404 fora dele.
#
#   powershell -File ferramentas\carga\semear-conversas.ps1
#   powershell -File ferramentas\carga\semear-conversas.ps1 -Quantas 100
#
# ASCII PURO, DE PROPOSITO: o powershell.exe 5.1 le arquivo UTF-8 sem BOM como ANSI, e um
# caractere acentuado numa string vira erro de parse - nao de execucao, de PARSE, o que derruba o
# script inteiro antes da primeira linha rodar.

param(
    [int]$Quantas = 60,
    [string]$Base = 'http://localhost:5123/api'
)

Add-Type -AssemblyName System.Net.Http
$http = New-Object System.Net.Http.HttpClient

# Contas de DESENVOLVIMENTO. A senha da demonstracao e constante publica do proprio codigo
# (ServicoSeedDemonstracao.Senha) - nao ha segredo aqui, e o script so fala com localhost.
$tenants = @(
    @{ nome = 'Padaria do Bairro'; email = 'ana@padaria.com';         senha = 'padaria-dev-2026' },
    @{ nome = 'Oficina Central';   email = 'ana.demo@nexora.exemplo'; senha = 'demonstracao-2026' }
)

function Postar($rota, $token) {
    $r = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$Base$rota")
    $r.Headers.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Bearer', $token)
    $r.Content = New-Object System.Net.Http.StringContent('{}', [Text.Encoding]::UTF8, 'application/json')
    $resp = $http.SendAsync($r).Result
    [pscustomobject]@{ Status = [int]$resp.StatusCode; Corpo = $resp.Content.ReadAsStringAsync().Result }
}

foreach ($t in $tenants) {
    Write-Host ""
    Write-Host "=========================================================="
    Write-Host " $($t.nome)"
    Write-Host "=========================================================="

    $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::Post, "$Base/auth/login")
    $corpo = @{ email = $t.email; senha = $t.senha } | ConvertTo-Json -Compress
    $req.Content = New-Object System.Net.Http.StringContent($corpo, [Text.Encoding]::UTF8, 'application/json')
    $login = $http.SendAsync($req).Result

    if ([int]$login.StatusCode -ne 200) {
        Write-Host "  login falhou ($([int]$login.StatusCode)) - pulando" -ForegroundColor Yellow
        continue
    }

    $token = ($login.Content.ReadAsStringAsync().Result | ConvertFrom-Json).token
    $r = Postar "/dev/semear-conversas?quantas=$Quantas" $token

    if ($r.Status -ne 200) {
        Write-Host "  falhou ($($r.Status)): $($r.Corpo)" -ForegroundColor Red
        continue
    }

    $d = $r.Corpo | ConvertFrom-Json
    Write-Host ("  {0} conversas | {1} mensagens criadas ({2} apagadas) | {3} nao entregues | {4} expiradas" -f $d.conversas, $d.mensagensCriadas, $d.mensagensApagadas, $d.naoEntregues, $d.expiradas)
}

Write-Host ""
