using Microsoft.EntityFrameworkCore;
using Nexora.Core;
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
/// O enforcement de PAPEL é do controller ([Authorize(Roles="dono")] nos PUT), pelo mesmo motivo.</summary>
public class ServicoConfiguracao(dra db) : IServicoConfiguracao
{
    public async Task<ConfiguracaoEmpresa> ObterAsync(CancellationToken ct) =>
        await db.Empresas.AsNoTracking()
            .Select(e => new ConfiguracaoEmpresa(
                e.Nome, e.Documento, e.FusoHorario, e.Uf,
                e.JanelaHoraInicio, e.JanelaHoraFim, e.JanelaDiasSemana,
                e.SemaforoAmareloMinutos, e.SemaforoVermelhoMinutos,
                e.DiasSemRespostaFollowUp))
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

        // NÃO reprocessa nada. Lembrete já criado mantém a data-alvo; mensagem já reservada
        // mantém o data_disparo. A configuração vale da próxima rodada em diante.
        await db.SaveChangesAsync(ct);
    }

    /// <summary>As validações, todas com mensagem voltada ao usuário final.
    ///
    /// Duas delas existem porque o valor "válido" para o banco é DESASTROSO para o produto, e o
    /// desastre é SILENCIOSO — está comentado em cada uma.</summary>
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
    }

    private async Task<Core.Entidades.Empresa> CarregarAsync(CancellationToken ct) =>
        await db.Empresas.FirstOrDefaultAsync(ct)
        ?? throw new RegraDeNegocioException("Empresa não encontrada.");
}
