using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Tempo;
using Nexora.Core.Seguranca;
using Nexora.Core.Whatsapp;
using Nexora.Infra.CloudApi;
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
    TimeProvider relogio,
    IClienteCloudApi cloud,
    CifraSegredos cifra) : IServicoConexoes
{
    private const int TamanhoMinimoNome = 2;
    private const int TamanhoMaximoNome = 40;

    // ==================================================================== listar
    public async Task<ConexoesDto> ListarAsync(CancellationToken ct)
    {
        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { Limite = (int)e.LimiteConexoes, e.CanalPadrao })
            .FirstOrDefaultAsync(ct);
        var limite = empresa == null ? 0 : empresa.Limite;
        var canalPadrao = empresa == null ? CanalWhatsapp.Evolution : empresa.CanalPadrao;

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
                c.Canal, c.PhoneNumberId, c.WabaId, c.VerifyToken, c.WebhookVerificadoEm,
                // So SE ha segredo — o valor nunca sai do banco por esta consulta.
                TokenConfigurado = c.AccessTokenCifrado != null,
                AppSecretConfigurado = c.AppSecretCifrado != null,
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
                c.Conversas, motivo is null, motivo,
                c.Canal.ParaApi(), c.PhoneNumberId, c.WabaId,
                c.TokenConfigurado, c.AppSecretConfigurado, c.VerifyToken, c.WebhookVerificadoEm);
        }).ToList();

        // `itens` é a lista INTEIRA (o teto do plano é de dezenas), então o tamanho dela é a contagem.
        return new ConexoesDto(itens, limite, itens.Count < limite, itens.Count, canalPadrao.ParaApi());
    }

    public async Task<ConexaoDto?> ObterAsync(long conexaoId, CancellationToken ct) =>
        (await ListarAsync(ct)).Itens.FirstOrDefault(c => c.Id == conexaoId);

    // ==================================================================== criar
    public async Task<long> CriarAsync(NovaConexao nova, CancellationToken ct)
    {
        var nome = ValidarNome(nova.Nome);

        var empresa = await db.Empresas.AsNoTracking()
            .Select(e => new { Limite = (int)e.LimiteConexoes, e.CanalPadrao })
            .FirstOrDefaultAsync(ct);
        var limite = empresa == null ? 0 : empresa.Limite;

        // Sem canal no pedido, vale o padrao da empresa (INT-XX).
        var canal = CanalDe(nova.Canal);
        if (canal == null) canal = empresa == null ? CanalWhatsapp.Evolution : empresa.CanalPadrao;

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

        if (canal == CanalWhatsapp.CloudApi)
            return await CriarCloudAsync(nome, nova, ct);

        var conexao = new Conexao
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            // Provisorio, trocado logo abaixo. Ver o bloco em SalvarComInstanciaDerivadaAsync.
            InstanceName = $"pendente-{Guid.NewGuid():N}",
            Status = StatusConexao.NaoCriada
        };

        db.Conexoes.Add(conexao);
        await SalvarComInstanciaDerivadaAsync(conexao, "emp", ct);
        return conexao.Id;
    }

    /// <summary>===================== A CONEXAO OFICIAL NASCE CONFERIDA (INT-XX) =====================
    ///
    /// Nada e gravado antes de a Meta confirmar tres coisas: o token le o numero, o numero e desta
    /// WABA, e o app ficou inscrito nos webhooks dela. Conferir na criacao tem dois motivos:
    ///   • o erro aparece no formulario, com a causa, e nao dias depois numa mensagem que nao saiu;
    ///   • o numero de OUTRA conta nao entra por aqui — o token precisa enxerga-lo na WABA dele.
    ///
    /// Os segredos sao cifrados antes do INSERT (ver `CifraSegredos`), e o `verify_token` e sorteado:
    /// e ele que o cliente cola no app da Meta para o webhook ser aceito.
    /// ============================================================================================</summary>
    private async Task<long> CriarCloudAsync(string nome, NovaConexao nova, CancellationToken ct)
    {
        var phoneNumberId = SoDigitos(nova.PhoneNumberId, "o Phone Number ID");
        var wabaId = SoDigitos(nova.WabaId, "o WABA ID");
        var token = Exigir(nova.AccessToken, "o token de acesso");
        var appSecret = Exigir(nova.AppSecret, "o app secret");

        // Antes da Meta: o indice unico pegaria isto depois, mas com um erro de banco em vez de uma
        // frase. ⚠️ A mensagem nao diz de QUEM e o numero — so que ja esta em uso.
        var emUso = await db.Conexoes.IgnoreQueryFilters()
            .AnyAsync(c => c.PhoneNumberId == phoneNumberId, ct);
        if (emUso)
            throw new RegraDeNegocioException("Este número da Meta já está cadastrado em uma conexão.", conflito: true);

        var numero = await cloud.LerNumeroAsync(phoneNumberId, token, ct);

        var naWaba = await cloud.NumeroEstaNaWabaAsync(wabaId, phoneNumberId, token, ct);
        if (!naWaba)
            throw new RegraDeNegocioException(
                "Este número não pertence a esta conta do WhatsApp Business. Confira o WABA ID.");

        await cloud.AssinarWebhooksAsync(wabaId, token, ct);

        var agora = relogio.GetUtcNow().UtcDateTime;
        var conexao = new Conexao
        {
            EmpresaId = contexto.EmpresaId,
            Nome = nome,
            Canal = CanalWhatsapp.CloudApi,
            InstanceName = $"pendente-{Guid.NewGuid():N}",
            PhoneNumberId = phoneNumberId,
            WabaId = wabaId,
            AccessTokenCifrado = cifra.Cifrar(token, FinalidadeSegredo.AccessToken),
            AppSecretCifrado = cifra.Cifrar(appSecret, FinalidadeSegredo.AppSecret),
            VerifyToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
            // ⚠️ SEM `Canonicalizar`: a Meta manda o numero COMPLETO, com o codigo do pais. A regra
            // de la (11 digitos = celular brasileiro sem o 55) transformava o numero de teste
            // americano +1 555 637 0179 em "5515556370179" — que nao existe no WhatsApp.
            Numero = numero.Numero,
            PerfilNome = numero.NomeVerificado,
            // A Meta acabou de responder pelo numero: ele esta no ar. Quem mantem isto daqui em
            // diante e a mesma conferencia de 5 minutos da Evolution.
            Status = StatusConexao.Conectado,
            StatusEm = agora,
            ConectadoEm = agora
        };

        db.Conexoes.Add(conexao);
        await SalvarComInstanciaDerivadaAsync(conexao, "cloud", ct);
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
    private async Task SalvarComInstanciaDerivadaAsync(Conexao conexao, string prefixo, CancellationToken ct)
    {
        var transacaoPropria = db.Database.CurrentTransaction is null;
        var tx = transacaoPropria ? await db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await db.SaveChangesAsync(ct);

            conexao.InstanceName = $"{prefixo}-{conexao.EmpresaId}-{conexao.Id}";
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

        // Persiste o status para o banner ficar fresco mesmo se o webhook se perder.
        var (estado, mudou) = await ConferenciaConexao.ConferirAsync(
            conexao, cliente, relogio.GetUtcNow().UtcDateTime, ct);

        if (mudou) await db.SaveChangesAsync(ct);

        return new StatusConexaoDto(conexao.InstanceName, estado, estado == "open");
    }

    /// <summary>Confere TODOS os números da empresa na Evolution e devolve a lista já corrigida.
    /// A tela chama isto UMA vez ao abrir — não é polling: o custo de N números por tick é o que
    /// tirou o poll contínuo desta tela no ARQ-2. Ver `ConferenciaConexao` para o porquê.</summary>
    public async Task<ConexoesDto> ConferirAsync(CancellationToken ct)
    {
        var agora = relogio.GetUtcNow().UtcDateTime;
        var mudou = false;

        foreach (var conexao in await db.Conexoes.OrderBy(c => c.Id).ToListAsync(ct))
            mudou |= (await ConferenciaConexao.ConferirAsync(conexao, cliente, agora, ct)).Mudou;

        if (mudou) await db.SaveChangesAsync(ct);

        return await ListarAsync(ct);
    }

    public async Task<QrCodeDto> ConectarAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        RecusarSeOficial(conexao);
        var qr = await cliente.ConectarInstanciaAsync(conexao.InstanceName, null, ct);
        return new QrCodeDto(qr.Base64, qr.Codigo, qr.PairingCode, qr.Estado, qr.Estado == "open");
    }

    public async Task<QrCodeDto> ParearAsync(long conexaoId, string numero, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        RecusarSeOficial(conexao);

        var canon = CanonicalizadorTelefone.Canonicalizar(numero ?? "");
        if (!CanonicalizadorTelefone.EhValido(canon))
            throw new RegraDeNegocioException("Informe o número com DDD para gerar o código de pareamento.");

        var qr = await cliente.ConectarInstanciaAsync(conexao.InstanceName, canon, ct);
        return new QrCodeDto(qr.Base64, qr.Codigo, qr.PairingCode, qr.Estado, qr.Estado == "open");
    }

    public async Task DesconectarAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        RecusarSeOficial(conexao);
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

        // ===================== "HOJE" É O DIA DA EMPRESA (AUD-XX, B6) =====================
        // Era a meia-noite de UTC: às 21h de Brasília o contador de "enviadas hoje" zerava, com o
        // dia de trabalho ainda aberto. O dia começa à meia-noite NO FUSO DA EMPRESA.
        // ==============================================================================
        var fusoHorario = await db.Empresas.AsNoTracking()
            .Select(e => e.FusoHorario)
            .FirstOrDefaultAsync(ct);
        var fuso = FusoDeNegocio.Resolver(fusoHorario);
        var hoje = DateOnly.FromDateTime(FusoDeNegocio.AgoraNo(relogio, fuso));
        var inicioDoDia = TimeZoneInfo.ConvertTimeToUtc(hoje.ToDateTime(TimeOnly.MinValue), fuso);

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

    // ==================================================================== Cloud API (INT-XX)
    public async Task AtualizarCredenciaisAsync(
        long conexaoId, CredenciaisCloud credenciais, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);
        if (conexao.Canal != CanalWhatsapp.CloudApi)
            throw new RegraDeNegocioException("Só conexões da API oficial têm token e app secret.", conflito: true);

        var token = (credenciais.AccessToken ?? "").Trim();
        var appSecret = (credenciais.AppSecret ?? "").Trim();

        // O token novo passa pela mesma conferencia da criacao ANTES de substituir o antigo: um
        // token colado errado nao pode derrubar uma conexao que estava funcionando.
        if (token.Length > 0)
        {
            await cloud.LerNumeroAsync(conexao.PhoneNumberId!, token, ct);
            var naWaba = await cloud.NumeroEstaNaWabaAsync(conexao.WabaId!, conexao.PhoneNumberId!, token, ct);
            if (!naWaba)
                throw new RegraDeNegocioException(
                    "Este token não enxerga o número na conta do WhatsApp Business desta conexão.");

            conexao.AccessTokenCifrado = cifra.Cifrar(token, FinalidadeSegredo.AccessToken);
        }

        if (appSecret.Length > 0)
            conexao.AppSecretCifrado = cifra.Cifrar(appSecret, FinalidadeSegredo.AppSecret);

        await db.SaveChangesAsync(ct);

        // O token novo acabou de ser conferido na Meta: o status reflete isso agora, e nao no
        // proximo giro do verificador.
        await AtualizarStatusAsync(conexao, ct);
    }

    /// <summary>===================== O STATUS NA HORA (BUG-XX) =====================
    /// O status da linha so era conferido ao ABRIR a tela e a cada 5 minutos (`VerificadorConexoes`).
    /// Quem colava um token novo e clicava "Testar" via o teste dar certo e o numero continuar
    /// "Desconectado" — a tela recarregava a lista, mas ninguem tinha perguntado de novo. Salvar
    /// credenciais e testar conferem na hora, pela MESMA conferencia da abertura da tela.
    ///
    /// Falha da conferencia nao derruba o teste nem a troca de token: o status fica como estava,
    /// e o teste ja lista o que falta.
    /// ==============================================================================</summary>
    private async Task AtualizarStatusAsync(Conexao conexao, CancellationToken ct)
    {
        try
        {
            var (_, mudou) = await ConferenciaConexao.ConferirAsync(
                conexao, cliente, relogio.GetUtcNow().UtcDateTime, ct);
            if (mudou) await db.SaveChangesAsync(ct);
        }
        catch (IntegracaoWhatsAppException)
        {
            // Ver acima: o status fica como estava.
        }
    }

    /// <summary>"Testar conexao". Na Evolution, e o estado da instancia. Na Cloud API, cada
    /// condicao para funcionar vira uma linha em portugues quando falta — o cliente configura o app
    /// da Meta sozinho, e "nao funciona" sem a causa nao o ajuda.</summary>
    public async Task<TesteConexaoDto> TestarAsync(long conexaoId, CancellationToken ct)
    {
        var conexao = await MinhaConexaoAsync(conexaoId, ct);

        if (conexao.Canal != CanalWhatsapp.CloudApi)
        {
            // A conferencia, e nao so a pergunta: o status da linha acompanha o que o teste viu.
            var (estado, mudou) = await ConferenciaConexao.ConferirAsync(
                conexao, cliente, relogio.GetUtcNow().UtcDateTime, ct);
            if (mudou) await db.SaveChangesAsync(ct);

            var problemas = new List<string>();
            if (estado != "open")
                problemas.Add("O número não está conectado. Conecte pelo QR Code.");
            return new TesteConexaoDto(problemas.Count == 0, conexao.Numero, conexao.PerfilNome, null, true, problemas);
        }

        var faltas = new List<string>();
        NumeroCloud? numero = null;
        var token = cifra.Decifrar(conexao.AccessTokenCifrado!, FinalidadeSegredo.AccessToken);

        try
        {
            numero = await cloud.LerNumeroAsync(conexao.PhoneNumberId!, token, ct);
            var naWaba = await cloud.NumeroEstaNaWabaAsync(conexao.WabaId!, conexao.PhoneNumberId!, token, ct);
            if (!naWaba)
                faltas.Add("O número não aparece mais nesta conta do WhatsApp Business.");
        }
        catch (IntegracaoWhatsAppException ex)
        {
            faltas.Add(ex.Message);
        }

        var webhookVerificado = conexao.WebhookVerificadoEm != null;
        if (!webhookVerificado)
            faltas.Add("A Meta ainda não confirmou o webhook: as mensagens recebidas não chegam. "
                     + "Cadastre a URL e o verify token no app da Meta.");

        await AtualizarStatusAsync(conexao, ct);

        // Completo como a Meta manda, pelo mesmo motivo da criacao.
        var numeroLido = numero == null ? conexao.Numero : numero.Numero;
        var nome = numero == null ? conexao.PerfilNome : numero.NomeVerificado;
        var qualidade = numero == null ? null : numero.Qualidade;

        return new TesteConexaoDto(faltas.Count == 0, numeroLido, nome, qualidade, webhookVerificado, faltas);
    }

    public async Task DefinirCanalPadraoAsync(string canal, CancellationToken ct)
    {
        var escolhido = CanalDe(canal);
        if (escolhido == null)
            throw new RegraDeNegocioException("Escolha o canal padrão: evolution ou cloud_api.");

        var empresa = await db.Empresas.FirstOrDefaultAsync(e => e.Id == contexto.EmpresaId, ct)
            ?? throw new RegraDeNegocioException("Empresa não encontrada.");

        empresa.CanalPadrao = escolhido.Value;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>QR, pareamento e desconectar sao da Evolution. Na Cloud API o numero e conectado
    /// na conta da Meta, e "desconectar" la e decisao do cliente, nao um botao daqui.</summary>
    private static void RecusarSeOficial(Conexao conexao)
    {
        if (conexao.Canal == CanalWhatsapp.CloudApi)
            throw new RegraDeNegocioException(
                "Conexão da API oficial não usa QR Code: o número é conectado na conta da Meta.",
                conflito: true);
    }

    /// <summary>O canal pelo rotulo da API. Nulo = nao informado; desconhecido = erro, para um
    /// erro de digitacao nao virar Evolution em silencio.</summary>
    private static CanalWhatsapp? CanalDe(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        if (texto == CanalWhatsapp.Evolution.ParaApi()) return CanalWhatsapp.Evolution;
        if (texto == CanalWhatsapp.CloudApi.ParaApi()) return CanalWhatsapp.CloudApi;
        throw new RegraDeNegocioException("Canal desconhecido. Use evolution ou cloud_api.");
    }

    /// <summary>Os ids da Meta sao so digitos. Conferir aqui impede que um `/` colado por engano
    /// mude o recurso pedido a Graph API.</summary>
    private static string SoDigitos(string? valor, string oQue)
    {
        var limpo = (valor ?? "").Trim();
        if (limpo.Length < 5 || limpo.Length > 30 || !limpo.All(char.IsAsciiDigit))
            throw new RegraDeNegocioException($"Informe {oQue}: só números, como aparece no painel da Meta.");
        return limpo;
    }

    private static string Exigir(string? valor, string oQue)
    {
        var limpo = (valor ?? "").Trim();
        if (limpo.Length == 0)
            throw new RegraDeNegocioException($"Informe {oQue}.");
        if (limpo.Length > 2048)
            throw new RegraDeNegocioException($"O valor informado para {oQue} é longo demais.");
        return limpo;
    }
}
