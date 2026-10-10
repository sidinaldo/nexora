using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>As configurações da empresa.
///
/// NÃO recebe IContextoEmpresa, e isso é deliberado: o query filter de `empresas` já é
/// `x.Id == _contexto.EmpresaId`, então `db.Empresas.First()` só pode devolver a empresa da
/// requisição. Injetar o contexto aqui só para reafirmar o tenant seria uma segunda regra de
/// isolamento sobre a mesma linha — e duas regras divergem no dia em que uma muda.
///
/// O enforcement de PAPEL é do controller (a politica `ConfigurarEmpresa` nos PUT), pelo mesmo motivo.</summary>
public class ServicoConfiguracao(NexoraDbContext db, ColetorAuditoria trilha)
    : IServicoConfiguracao
{
    public async Task<ConfiguracaoEmpresa> ObterAsync(CancellationToken ct) =>
        await db.Empresas.AsNoTracking()
            .Select(e => new ConfiguracaoEmpresa(
                e.Nome, e.Documento, e.FusoHorario, e.Uf,
                e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana,
                e.SemaforoAmareloMinutos, e.SemaforoVermelhoMinutos,
                e.DiasSemRespostaFollowUp, e.FollowUpTexto, e.DiasParaConcluirVenda, e.ConclusaoAutomatica,
                e.NpsAtivo, e.NpsDiasAposConclusao, e.NpsDiasExpiracao, e.NpsTexto,
                e.NpsMensagemPromotor, e.NpsMensagemDetrator, e.ResumoDiarioAtivo))
            .FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException("Empresa não encontrada.");

    public async Task AtualizarDadosAsync(EditarDadosEmpresa dados, CancellationToken ct)
    {
        var empresa = await CarregarAsync(ct);

        var nome = (dados.Nome ?? "").Trim();
        if (nome.Length == 0)
            throw new RegraDeNegocioException("Informe o nome da empresa.");

        var documento = new string((dados.Documento ?? "").Where(char.IsDigit).ToArray());
        if (documento.Length > 0 && documento.Length != 11 && documento.Length != 14)
            throw new RegraDeNegocioException(
                "O documento precisa ser um CPF (11 dígitos) ou um CNPJ (14 dígitos).");

        empresa.Nome = nome;
        empresa.Documento = documento.Length == 0 ? null : documento;
        empresa.FusoHorario = ValidarFuso(dados.FusoHorario);
        empresa.Uf = ValidarUf(dados.Uf);

        // NÃO reprocessa nada. Lembrete já reservado mantém a data-alvo e mensagem já reservada
        // mantém o data_disparo — os dois foram carimbados com o fuso antigo. A tela avisa.
        await db.SaveChangesAsync(ct);
    }

    /// <summary>===================== POR QUE VALIDAR AQUI DÓI SE FALTAR =====================
    /// `FusoDeNegocio.Resolver` cai em UTC-3 quando o id não existe no host, e cai EM SILÊNCIO —
    /// é o comportamento certo lá (container alpine sem tzdata não pode derrubar o agendador),
    /// mas seria péssimo aqui: a empresa de Manaus salvaria "America/Manaus" com erro de digitação,
    /// a tela mostraria o valor salvo, e a rodada dispararia uma hora errada para sempre. Sem erro
    /// nenhum para investigar.
    ///
    /// Então a escrita é ESTRITA onde a leitura é tolerante: id desconhecido é recusado na cara.
    /// ============================================================================</summary>
    private static string ValidarFuso(string? id)
    {
        var alvo = (id ?? "").Trim();
        if (alvo.Length == 0)
            throw new RegraDeNegocioException("Informe o fuso horário.");

        try
        {
            // A checagem é contra o HOST, não contra a lista da tela: é o host que o
            // `FusoDeNegocio` vai consultar em produção, e é a resposta dele que importa.
            TimeZoneInfo.FindSystemTimeZoneById(alvo);
            return alvo;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new RegraDeNegocioException(
                $"O fuso horário \"{alvo}\" não existe neste servidor. " +
                "Escolha um da lista — um fuso inválido faria os follow-ups dispararem na hora errada.");
        }
    }

    private static string? ValidarUf(string? uf)
    {
        var alvo = (uf ?? "").Trim().ToUpperInvariant();
        if (alvo.Length == 0) return null;   // sem UF = só feriados nacionais, e está certo

        if (!ConfiguracaoRef.Ufs.Contains(alvo))
            throw new RegraDeNegocioException($"UF inválida: \"{alvo}\".");

        return alvo;
    }

    /// <summary>Os fusos da tela, com o offset ATUAL calculado no host. Mostrar o offset evita a
    /// dúvida mais comum ("Manaus é -4 mesmo?") sem obrigar ninguém a decorar.
    ///
    /// A lista é filtrada pelo que o host de fato conhece: oferecer um id que o servidor não tem
    /// levaria o dono direto ao erro de validação, por culpa nossa.</summary>
    public IReadOnlyList<FusoDisponivel> FusosDisponiveis()
    {
        var disponiveis = new List<FusoDisponivel>();

        foreach (var (id, rotulo) in ConfiguracaoRef.FusosBrasil)
        {
            try
            {
                var fuso = TimeZoneInfo.FindSystemTimeZoneById(id);
                var offset = fuso.GetUtcOffset(DateTimeOffset.UtcNow);
                var sinal = offset < TimeSpan.Zero ? "-" : "+";
                disponiveis.Add(new FusoDisponivel(
                    id, rotulo, $"UTC{sinal}{Math.Abs(offset.Hours):D2}:{Math.Abs(offset.Minutes):D2}"));
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Host sem tzdata completo: o fuso simplesmente não é oferecido.
            }
        }

        return disponiveis;
    }

    public async Task AtualizarAtendimentoAsync(EditarAtendimento dados, CancellationToken ct)
    {
        Validar(dados);

        var empresa = await CarregarAsync(ct);
        empresa.JanelaHoraInicio = dados.JanelaHoraInicio;
        empresa.JanelaHoraFim = dados.JanelaHoraFim;
        empresa.JanelaDiasSemana = dados.JanelaDiasSemana;
        empresa.SemaforoAmareloMinutos = dados.SemaforoAmareloMinutos;
        empresa.SemaforoVermelhoMinutos = dados.SemaforoVermelhoMinutos;
        empresa.DiasSemRespostaFollowUp = dados.DiasSemRespostaFollowUp;
        empresa.DiasParaConcluirVenda = dados.DiasParaConcluirVenda;
        // O `!` é seguro: `Validar` recusou nulo acima. Ver o comentário em `EditarAtendimento`.
        empresa.ConclusaoAutomatica = dados.ConclusaoAutomatica!.Value;
        if (dados.FollowUpTexto != null) empresa.FollowUpTexto = dados.FollowUpTexto.Trim();

        // NÃO reprocessa nada. Lembrete já criado mantém a data-alvo; mensagem já reservada
        // mantém o data_disparo. A configuração vale da próxima rodada em diante.
        await db.SaveChangesAsync(ct);
    }

    /// <summary>As validações, todas com mensagem voltada ao usuário final.
    ///
    /// Duas delas existem porque o valor "válido" para o banco é DESASTROSO para o produto, e o
    /// desastre é SILENCIOSO — está comentado em cada uma.</summary>
    public async Task AtualizarResumoDiarioAsync(bool ativo, CancellationToken ct)
    {
        var empresa = await CarregarAsync(ct);
        empresa.ResumoDiarioAtivo = ativo;
        await db.SaveChangesAsync(ct);
    }

    public async Task AtualizarPesquisaNpsAsync(EditarPesquisaNps dados, CancellationToken ct)
    {
        Validar(dados);

        var empresa = await CarregarAsync(ct);

        // ===================== A TRILHA DO QUE O CLIENTE VAI RECEBER =====================
        // ⚠️ `AtualizarAtendimentoAsync` NAO DECLARA NADA, e a assimetria e deliberada: isto decide
        // MENSAGEM SAINDO para cliente, e "quem mudou o texto que o cliente recebeu" e pergunta que
        // aparece depois de a mensagem chegar errada. Os limites do semaforo nao tem esse peso.
        //
        // O de/para EXPLICITO em `nps_ativo` e no texto: a trilha monta o diff sozinha para
        // entidade rastreada, mas estes dois sao os que alguem vai querer ler, e sem eles a linha
        // diria apenas "a configuracao mudou".
        // ================================================================================
        trilha.Declarar(
            EntidadeAuditada.Empresa, empresa.Id, AcaoAuditoria.Editou,
            new Dictionary<string, AlteracaoValor>
            {
                ["npsAtivo"] = new(empresa.NpsAtivo, dados.NpsAtivo!.Value),
                ["npsTexto"] = new(empresa.NpsTexto, dados.NpsTexto.Trim())
            });

        empresa.NpsAtivo = dados.NpsAtivo!.Value;
        empresa.NpsDiasAposConclusao = dados.NpsDiasAposConclusao;
        empresa.NpsDiasExpiracao = dados.NpsDiasExpiracao;
        empresa.NpsTexto = dados.NpsTexto.Trim();

        // ⚠️ VAZIO VIRA NULO, e os dois querem dizer a mesma coisa: nao envia. Guardar string em
        // branco deixaria o campo dizendo "ha uma mensagem" sem mensagem nenhuma — a mesma decisao
        // do `CancelamentoMotivo` no `ServicoVendas`.
        empresa.NpsMensagemPromotor = Opcional(dados.NpsMensagemPromotor);
        empresa.NpsMensagemDetrator = Opcional(dados.NpsMensagemDetrator);

        // NAO REPROCESSA NADA, como a vizinha de atendimento: pesquisa ja agendada mantem a
        // `data_agendada` e a `data_limite` — elas congelam no nascimento justamente para a
        // configuracao nao mover o limite de uma pesquisa viva. O texto novo vale no proximo ENVIO.
        await db.SaveChangesAsync(ct);
    }

    private static string? Opcional(string? texto) =>
        string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    /// <summary>===================== OS LIMITES DA PESQUISA =====================
    ///
    /// ⚠️ O TEXTO VAZIO E O PIOR DOS CASOS, e por isso e o primeiro: a coluna e NOT NULL, entao o
    /// banco aceitaria `''` — e sairia uma mensagem EM BRANCO no WhatsApp do cliente. Nenhum erro,
    /// nenhum log: so um balao vazio chegando de quem ele comprou.
    ///
    /// ⚠️ O TETO DE DIAS NAO E CAPRICHO. `MotorNps` so agenda pesquisa cuja `data_limite` ainda nao
    /// passou, e a `data_limite` e `conclusao + dias + 7`. Com `dias` absurdo, a pesquisa de hoje
    /// seria agendada para o ano que vem e ficaria viva na tabela sem nunca sair.
    /// ==================================================================</summary>
    private static void Validar(EditarPesquisaNps d)
    {
        // `bool?` recusando nulo: ver o comentario em `EditarPesquisaNps` para por que ele e
        // anulavel. Sem esta linha, o campo omitido DESLIGARIA a pesquisa em silencio.
        if (d.NpsAtivo is null)
            throw new RegraDeNegocioException("Diga se a pesquisa está ligada ou desligada.");

        if (string.IsNullOrWhiteSpace(d.NpsTexto))
            throw new RegraDeNegocioException("Escreva a pergunta da pesquisa.");

        if (d.NpsTexto.Trim().Length > LimiteDeTexto)
            throw new RegraDeNegocioException(
                $"A pergunta precisa ter até {LimiteDeTexto} caracteres.");

        if (d.NpsDiasAposConclusao is < 0 or > TetoDeDias)
            throw new RegraDeNegocioException(
                $"Os dias até a pergunta precisam estar entre 0 e {TetoDeDias}.");

        // ⚠️ MINIMO UM: com zero, a pesquisa expiraria no mesmo instante em que fosse enviada — e o
        // cliente receberia uma pergunta que o sistema ja desistiu de ler.
        if (d.NpsDiasExpiracao is < 1 or > TetoDeDias)
            throw new RegraDeNegocioException(
                $"Os dias de espera pela nota precisam estar entre 1 e {TetoDeDias}.");

        foreach (var opcional in new[] { d.NpsMensagemPromotor, d.NpsMensagemDetrator })
        {
            if ((opcional ?? "").Trim().Length > LimiteDeTexto)
                throw new RegraDeNegocioException(
                    $"As mensagens de agradecimento precisam ter até {LimiteDeTexto} caracteres.");
        }
    }

    /// <summary>Teto de caracteres de cada texto. O WhatsApp aceita muito mais; o freio e de
    /// digitacao — mensagem de pesquisa com mil caracteres nao e lida.</summary>
    public const int LimiteDeTexto = 500;

    /// <summary>Teto de dias, tanto para a espera quanto para o prazo. Ver `Validar` para por que
    /// um numero absurdo deixa pesquisa viva sem nunca sair.</summary>
    public const int TetoDeDias = 90;

    private static void Validar(EditarAtendimento d)
    {
        if (d.JanelaHoraInicio is < 0 or > 23)
            throw new RegraDeNegocioException("A hora de abertura precisa estar entre 0 e 23.");

        if (d.JanelaHoraFim is < 1 or > 24)
            throw new RegraDeNegocioException("A hora de fechamento precisa estar entre 1 e 24.");

        // ck_empresas_janela também barra no banco; aqui a mensagem é legível.
        if (d.JanelaHoraInicio >= d.JanelaHoraFim)
            throw new RegraDeNegocioException(
                "O horário de abertura precisa ser antes do de fechamento.");

        // ===== NENHUM DIA MARCADO =====
        // O banco também barra (ck_empresas_dias exige BETWEEN 1 AND 127) — mas com mensagem de
        // constraint, não de gente. E o estrago, se passasse, seria SILENCIOSO: a empresa nunca
        // atenderia, então nenhum follow-up dispararia e o semáforo nunca acenderia. Sem erro,
        // sem log, sem nada para investigar. O dono só descobriria pelo faturamento.
        if (d.JanelaDiasSemana is < 1 or > 127)
            throw new RegraDeNegocioException(
                "Marque pelo menos um dia da semana em que a empresa atende.");

        if (d.SemaforoAmareloMinutos < 0 || d.SemaforoVermelhoMinutos < 0)
            throw new RegraDeNegocioException("Os tempos do semáforo não podem ser negativos.");

        // ZERO DESLIGA a faixa, e isso é legítimo — quem não quer o alerta amarelo põe zero. Só
        // não pode ficar fora de ordem: vermelho antes do amarelo faria a conversa pular o
        // amarelo e nascer vermelha.
        if (d.SemaforoVermelhoMinutos > 0 && d.SemaforoAmareloMinutos > d.SemaforoVermelhoMinutos)
            throw new RegraDeNegocioException(
                "O tempo do alerta vermelho precisa ser maior que o do amarelo.");

        // ===== ZERO DIA DE INATIVIDADE =====
        // Geraria follow-up para conversa respondida HOJE — o robô escrevendo para quem acabou
        // de ser atendido. É o caminho mais rápido para o número ser denunciado.
        if (d.DiasSemRespostaFollowUp < 1)
            throw new RegraDeNegocioException(
                "O follow-up precisa de pelo menos 1 dia de conversa parada.");

        if (d.DiasSemRespostaFollowUp > 365)
            throw new RegraDeNegocioException("O follow-up aceita no máximo 365 dias.");

        // Nulo mantém o texto (ver `EditarAtendimento`); em branco seria uma mensagem vazia no
        // WhatsApp do cliente.
        if (d.FollowUpTexto != null && string.IsNullOrWhiteSpace(d.FollowUpTexto))
            throw new RegraDeNegocioException("Escreva o texto do follow-up.");

        if (d.FollowUpTexto != null && d.FollowUpTexto.Trim().Length > LimiteDeTexto)
            throw new RegraDeNegocioException(
                $"O texto do follow-up precisa ter até {LimiteDeTexto} caracteres.");

        // ===== O PRAZO DE CONCLUSÃO (NEG-2) =====
        // ZERO É VÁLIDO e não é descuido: significa "concluir na hora", e é o ajuste certo para
        // quem vende no balcão. O teto de 90 é o outro extremo — acima disso a coluna volta a
        // acumular na prática, que é o problema que o bloco existe para resolver. `ck_empresas_
        // conclusao` repete o intervalo no banco.
        if (d.DiasParaConcluirVenda is < 0 or > 90)
            throw new RegraDeNegocioException(
                "O prazo para concluir a venda vai de 0 a 90 dias. Zero conclui na hora.");

        // ===== O CAMPO OMITIDO NÃO PODE DESLIGAR A FEATURE (POS-1) =====
        // `bool` omitido vira `false`, que é válido e silencioso. É a única validação deste método
        // que não protege uma FAIXA — ela protege contra o default do tipo significar uma escolha.
        if (d.ConclusaoAutomatica is null)
            throw new RegraDeNegocioException(
                "Informe se a conclusão automática da venda está ligada.");
    }

    private async Task<Core.Entidades.Empresa> CarregarAsync(CancellationToken ct) =>
        await db.Empresas.FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException("Empresa não encontrada.");
}
