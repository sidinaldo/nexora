using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>As conexoes de WhatsApp do tenant logado.
///
/// O isolamento aqui e o HasQueryFilter global: toda consulta a Conexoes ja sai filtrada pela
/// empresa do JWT. Diferente do processador do webhook, este servico roda DENTRO de requisicao
/// autenticada — nao ha IgnoreQueryFilters nenhum, e nao deve haver. Por consequencia, id de
/// outra empresa simplesmente nao existe daqui: vira "Conexao nao encontrada", que e a resposta
/// certa e nao revela que a linha existe em outro tenant.</summary>
public class ServicoConexoes(
    NexoraDbContext db,
    IClienteWhatsApp cliente,
    IContextoEmpresa contexto,
    TimeProvider relogio) : IServicoConexoes
{
    private const int TamanhoMinimoNome = 2;
    private const int TamanhoMaximoNome = 40;

    // ==================================================================== listar
    public async Task<ConexoesDto> ListarAsync(CancellationToken ct)
    {
        var limite = await db.Empresas.AsNoTracking()
            .Select(e => (int)e.LimiteConexoes)
            .FirstOrDefaultAsync(ct);

        // O `Conversas` e a contagem CRUA, pelo mesmo motivo que a de contatos por etapa: e o
        // numero que responde "o que trava a remocao", nao "o que aparece na caixa". Contar so as
        // abertas mostraria zero numa conexao que a FK recusa apagar, e o dono levaria o erro
        // depois do clique.
        var linhas = await db.Conexoes.AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.Nome, c.InstanceName, c.Numero, c.NumeroAnterior,
                c.PerfilNome, c.PerfilFotoUrl, c.Status, c.ConectadoEm, c.DesconectadoEm,
                Conversas = db.Conversas.Count(v => v.ConexaoId == c.Id),
                TemMensagem = db.Mensagens.Any(m => m.ConexaoId == c.Id)
            })
            .ToListAsync(ct);

        var itens = linhas.Select(c =>
        {
            var motivo = MotivoParaNaoRemover(c.Conversas, c.TemMensagem, linhas.Count);
            return new ConexaoDto(
                c.Id, c.Nome, c.InstanceName, c.Numero, c.NumeroAnterior,
                c.PerfilNome, c.PerfilFotoUrl, c.Status.ParaApi(),
                c.ConectadoEm, c.DesconectadoEm,
                c.Conversas, motivo is null, motivo);
        }).ToList();

        return new ConexoesDto(itens, limite, itens.Count < limite);
    }

    public async Task<ConexaoDto?> ObterAsync(long conexaoId, CancellationToken ct) =>
        (await ListarAsync(ct)).Itens.FirstOrDefault(c => c.Id == conexaoId);

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovaConexao nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        var limite = await db.Empresas.AsNoTracking()
            .Select(e => (int)e.LimiteConexoes)
            .FirstOrDefaultAsync(ct);

        var existentes = await db.Conexoes.AsNoTracking()
            .Select(c => new { c.Id, c.Nome })
            .ToListAsync(ct);

        // ===================== O LIMITE E DA APLICACAO, DE PROPOSITO =====================
        // Ate o ARQ-2 quem impedia a segunda conexao era o indice `uq_conexoes_empresa`. Ele saiu:
        // o teto vem do CONTRATO (`empresas.limite_conexoes`), e numero que muda por contrato nao
        // pode morar num indice — subir de plano viraria migration.
        //
        // Consequencia honesta: sem indice, dois pedidos simultaneos podem passar os dois pela
        // contagem e criar uma conexao a mais. Nao ha lock aqui de proposito — o pedido parte do
        // dono, numa tela de configuracao, um clique por vez; e o dano de uma conexao extra e uma
        // linha a remover, nao dado corrompido. Se um dia isso importar, o lugar de resolver e um
        // advisory lock por empresa, nao um indice.
        // ================================================================================
        if (existentes.Count >= limite)
            throw new RegraDeNegocioException(
                limite == 1
                    ? "Seu plano permite um número de WhatsApp. Fale com o suporte para conectar mais."
                    : $"Seu plano permite {limite} números de WhatsApp, e todos já estão em uso.",
                conflito: true);

        ExigirNomeLivre(existentes.Select(c => (c.Id, c.Nome)), nome, ignorarId: null);

        var conexao = new Conexao
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            // Provisorio, trocado logo abaixo. Ver o bloco em SalvarComInstanciaDerivadaAsync.
            InstanceName = $"pendente-{Guid.NewGuid():N}",
            Status = StatusConexao.NaoCriada
        };

        db.Conexoes.Add(conexao);
        await SalvarComInstanciaDerivadaAsync(conexao, ct);
        return conexao.Id;
    }

    /// <summary>Grava a conexao e so entao carimba o `instance_name` definitivo, derivado do id.
    ///
    /// ===================== POR QUE DUAS PASSADAS =====================
    /// O `instance_name` precisa de tres coisas ao mesmo tempo: ser unico globalmente
    /// (`uq_conexoes_instance`), NUNCA ser reaproveitado depois de uma remocao, e ser legivel para
    /// quem abre o painel da Evolution durante um suporte.
    ///
    /// O id da conexao da as tres: e sequencial (identity always), nunca volta atras, e cabe num
    /// nome curto — `emp-7-3`. So que ele so existe DEPOIS do INSERT, e a coluna e NOT NULL. Dai o
    /// nome provisorio com Guid, unico o bastante para nao colidir com nada no meio do caminho, e
    /// a segunda passada dentro da MESMA transacao: se ela falhar, a primeira volta atras e nao
    /// sobra linha com nome de rascunho.
    ///
    /// Reaproveitar nome apos remocao seria pior que feio: a instancia antiga pode ainda existir
    /// do lado da Evolution, e a conexao nova adotaria a sessao dela em silencio.
    /// =================================================================</summary>
    private async Task SalvarComInstanciaDerivadaAsync(Conexao conexao, CancellationToken ct)
    {
        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await db.SaveChangesAsync(ct);

            conexao.InstanceName = $"emp-{conexao.EmpresaId}-{conexao.Id}";
            await db.SaveChangesAsync(ct);

            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== renomear
    public async Task RenomearAsync(long conexaoId, string nome, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        var limpo = ValidarNome(nome);

        var outras = await db.Conexoes.AsNoTracking()
            .Select(c => new { c.Id, c.Nome })
            .ToListAsync(ct);
        ExigirNomeLivre(outras.Select(c => (c.Id, c.Nome)), limpo, ignorarId: conexaoId);

        // SO o nome. `instance_name` fica onde esta — ver o bloco em IServicoConexoes: renomear a
        // instancia orfanaria a sessao na Evolution e o webhook pararia de achar o tenant EM
        // SILENCIO, sem erro e sem log, ate alguem reclamar que o cliente nao foi respondido.
        conexao.Nome = limpo;
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== remover
    public async Task RemoverAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);

        var quantas = await db.Conexoes.CountAsync(ct);
        var conversas = await db.Conversas.CountAsync(c => c.ConexaoId == conexaoId, ct);
        var temMensagem = await db.Mensagens.AnyAsync(m => m.ConexaoId == conexaoId, ct);

        // As FKs de conversas e mensagens sao RESTRICT, entao o banco recusaria de qualquer jeito.
        // Mas erro de FK nao e fluxo de controle: viraria 500 numa tela de configuracao. A
        // pergunta e feita ANTES, e a resposta e a MESMA que a lista ja mostrou no botao.
        if (MotivoParaNaoRemover(conversas, temMensagem, quantas) is { } motivo)
            throw new RegraDeNegocioException(motivo, conflito: true);

        // ===================== A EVOLUTION PRIMEIRO, E NAO POR ACASO =====================
        // Se a linha fosse apagada antes, uma falha aqui deixaria a instancia viva do outro lado —
        // pareada, mandando webhook que ninguem reconhece — e sem o nome guardado em lugar nenhum
        // para alguem limpar depois. Vazamento silencioso e irrecuperavel.
        //
        // Na ordem contraria, o pior caso e a linha sobreviver apontando para uma instancia que ja
        // nao existe. Isso o dono ve na tela, e a operacao e idempotente: clicar de novo resolve.
        // Erro visivel e recuperavel ganha de erro invisivel, sempre.
        //
        // A conexao aqui nao tem historico — foi a condicao para chegar ate esta linha —, entao
        // recusar a remocao enquanto a Evolution estiver fora nao bloqueia atendimento nenhum.
        // ================================================================================
        await cliente.RemoverInstanciaAsync(conexao.InstanceName, ct);

        db.Conexoes.Remove(conexao);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Por que esta conexao NAO pode ser removida, ou null se pode.
    ///
    /// Vive num lugar so porque a lista e a remocao precisam responder a MESMA coisa: a tela
    /// desabilita o botao com este texto, e o servico recusa com ele. Duas copias divergiriam, e o
    /// sintoma seria um botao habilitado que devolve erro — a pior forma de dizer "nao pode".</summary>
    private static string? MotivoParaNaoRemover(int conversas, bool temMensagem, int totalDeConexoes)
    {
        // A invariante que o banco NAO garante. Sem nenhuma conexao o webhook nao acha o tenant
        // (ele casa por instance_name), o envio nao tem instancia, e NADA no sistema recria uma —
        // a criacao so acontece no cadastro da empresa. A conta ficaria sem caminho de volta.
        if (totalDeConexoes <= 1)
            return "Esta é a única conexão da empresa. Sem ela nenhuma mensagem entra ou sai, "
                 + "e não há como recriá-la pela tela.";

        if (conversas > 0)
            return $"Este número tem {conversas} {(conversas == 1 ? "conversa" : "conversas")} "
                 + "no histórico. Apagar perderia o atendimento — desconecte em vez de apagar.";

        if (temMensagem)
            return "Este número tem mensagens no histórico. Apagar perderia o atendimento — "
                 + "desconecte em vez de apagar.";

        return null;
    }

    // ==================================================================== pareamento
    public async Task<StatusConexaoDto> StatusAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        var estado = await cliente.StatusInstanciaAsync(conexao.InstanceName, ct);
        var conectado = estado == "open";

        // Persiste o status para o banner ficar fresco mesmo se o webhook se perder. So escreve
        // quando MUDA — a tela chama isto em polling de 3s, e sem o guard seria uma escrita a
        // cada tick.
        //
        // 'offline' e distinto de 'desconectado': offline = a Evolution nao respondeu (problema
        // nosso, o cliente nao tem o que fazer); desconectado = o numero caiu e ele precisa
        // reparear. Colapsar os dois manda a pessoa escanear QR a toa.
        var novo = estado switch
        {
            "open" => StatusConexao.Conectado,
            "connecting" => StatusConexao.Conectando,
            "nao_criada" => StatusConexao.NaoCriada,
            "offline" => StatusConexao.Offline,
            _ => StatusConexao.Desconectado
        };

        var agora = relogio.GetUtcNow().UtcDateTime;
        var mudou = false;

        if (conexao.Status != novo)
        {
            conexao.Status = novo;
            conexao.StatusEm = agora;
            if (conectado && conexao.ConectadoEm is null) conexao.ConectadoEm = agora;
            if (!conectado && novo == StatusConexao.Desconectado) conexao.DesconectadoEm = agora;
            mudou = true;
        }

        // Rede de seguranca: conectada mas sem numero (webhook connection.update perdido) ->
        // backfill do numero e do perfil.
        if (conectado && string.IsNullOrEmpty(conexao.Numero))
        {
            var det = await cliente.ObterDetalhesInstanciaAsync(conexao.InstanceName, ct);
            if (det?.OwnerJid is { Length: > 0 } jid)
            {
                conexao.Numero = CanonicalizadorTelefone.Canonicalizar(jid.Split('@')[0]);
                conexao.PerfilNome ??= det.PerfilNome;
                conexao.PerfilFotoUrl ??= det.PerfilFotoUrl;
                mudou = true;
            }
        }

        if (mudou) await db.SaveChangesAsync(ct);

        return new StatusConexaoDto(conexao.InstanceName, estado, conectado);
    }

    public async Task<QrCodeDto> ConectarAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        var qr = await cliente.ConectarInstanciaAsync(conexao.InstanceName, null, ct);
        return new QrCodeDto(qr.Base64, qr.Codigo, qr.PairingCode, qr.Estado, qr.Estado == "open");
    }

    public async Task<QrCodeDto> ParearAsync(long conexaoId, string numero, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);

        var canon = CanonicalizadorTelefone.Canonicalizar(numero ?? "");
        if (!CanonicalizadorTelefone.EhValido(canon))
            throw new RegraDeNegocioException("Informe o número com DDD para gerar o código de pareamento.");

        var qr = await cliente.ConectarInstanciaAsync(conexao.InstanceName, canon, ct);
        return new QrCodeDto(qr.Base64, qr.Codigo, qr.PairingCode, qr.Estado, qr.Estado == "open");
    }

    public async Task DesconectarAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        await cliente.DesconectarInstanciaAsync(conexao.InstanceName, ct);

        // Reflete de imediato. O webhook connection.update confirma depois, mas nao dependemos
        // dele: se ele se perder, a tela ficaria mostrando "conectado" para sempre.
        if (conexao.Status != StatusConexao.Desconectado)
        {
            var agora = relogio.GetUtcNow().UtcDateTime;
            conexao.Status = StatusConexao.Desconectado;
            conexao.StatusEm = agora;
            conexao.DesconectadoEm = agora;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task ReconhecerTrocaAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        if (conexao.NumeroAnterior is null) return;
        conexao.NumeroAnterior = null;
        await db.SaveChangesAsync(ct);
    }

    // ==================================================================== saude
    public async Task<SaudeConexaoDto> SaudeAsync(long conexaoId, CancellationToken ct)
    {
        // Valida que a conexao e desta empresa ANTES de contar. Sem isso, id de outro tenant
        // devolveria zeros — resposta que parece legitima e esconde que a pergunta era invalida.
        _ = await MinhaConexaoAsync(conexaoId, ct);

        var agora = relogio.GetUtcNow().UtcDateTime;
        var inicioDoDia = new DateTime(agora.Year, agora.Month, agora.Day, 0, 0, 0, DateTimeKind.Utc);

        // ===================== POR CONEXAO, NAO POR EMPRESA =====================
        // Ate o ARQ-2 estes numeros eram da empresa inteira, e com um numero so isso dava no
        // mesmo. Com N, o total ESCONDE justamente o que interessa: quando um dos numeros cai, a
        // soma continua parecendo saudavel por causa dos outros. Quem abre esta tela quer saber
        // QUAL numero esta falhando.
        // =======================================================================
        var saidas = db.Mensagens.AsNoTracking()
            .Where(m => m.Direcao == DirecaoMensagem.Saida && m.ConexaoId == conexaoId);

        return new SaudeConexaoDto(
            EnviadasHoje: await saidas.CountAsync(m => m.EnviadaEm >= inicioDoDia, ct),

            // Ainda vai ser tentada: reservada, nao despachada, nao expirada.
            Pendentes: await saidas.CountAsync(
                m => m.EnviadaEm == null && m.ExpiradaEm == null && m.LembreteId != null, ct),

            // Passou da janela de reenvio: NAO sera mais tentada. E o numero que exige acao
            // humana, e por isso nao pode ficar somado ao de cima.
            Expiradas: await saidas.CountAsync(m => m.ExpiradaEm != null, ct),

            FalhasHoje: await saidas.CountAsync(
                m => m.Erro != null && m.EnviadaEm == null && m.ReservadoEm >= inicioDoDia, ct));
    }

    // ==================================================================== apoio

    /// <summary>O query filter ja recorta por empresa; o nulo vira "nao encontrada", que e a
    /// resposta certa tanto para id inexistente quanto para id de outro tenant.</summary>
    private async Task<Conexao> MinhaConexaoAsync(long conexaoId, CancellationToken ct) =>
        await db.Conexoes.FirstOrDefaultAsync(c => c.Id == conexaoId, ct)
            ?? throw new RegraDeNegocioException("Conexão não encontrada.");

    private static string ValidarNome(string? nome)
    {
        var limpo = (nome ?? "").Trim();
        if (limpo.Length < TamanhoMinimoNome)
            throw new RegraDeNegocioException(
                $"Dê um nome à conexão (mínimo {TamanhoMinimoNome} caracteres).");
        return limpo.Length <= TamanhoMaximoNome ? limpo : limpo[..TamanhoMaximoNome];
    }

    /// <summary>Nome repetido nao corrompe nada — mas a tela virou uma LISTA, e duas linhas
    /// "Principal" tornam impossivel saber qual numero e qual na hora de apagar.
    /// `uq_conexoes_empresa_nome` cobre o caso exato; a checagem aqui pega tambem a diferenca so
    /// de caixa, que o indice deixaria passar e o olho nao distingue.</summary>
    private static void ExigirNomeLivre(
        IEnumerable<(long Id, string Nome)> existentes, string nome, long? ignorarId)
    {
        if (existentes.Any(c => c.Id != ignorarId
                             && string.Equals(c.Nome, nome, StringComparison.OrdinalIgnoreCase)))
            throw new RegraDeNegocioException($"Já existe uma conexão chamada \"{nome}\".", conflito: true);
    }
}
