namespace Nexora.Core.Resumo;

/// <summary>Um motivo de mensagem automatica que nao saiu, e quantas vezes.</summary>
public record MotivoDeFalha(string Motivo, int Quantas);

/// <summary>===================== O DIA ANTERIOR, PARA O DONO (RES-XX) =====================
///
/// O que aconteceu ONTEM (no fuso da empresa) e o que espera AGORA, no e-mail das 8h. A empresa
/// inteira: o resumo e do dono, e mostra o que ele ve na tela.
///
/// ⚠️ OS NUMEROS SAO OS DO PAINEL. Leads e vendas saem dos Relatorios; "esperando resposta" e
/// "lembretes de hoje", do Painel inicial. Um e-mail que dissesse 5 leads e a tela 4 desmoralizaria
/// os dois. So as automaticas e as respostas da pesquisa tem conta propria — a tela nao as mostra
/// por dia.
/// =================================================================================</summary>
public record ResumoDiario(
    DateOnly Dia,
    string Empresa,
    int LeadsNovos,
    int Vendas,
    decimal ValorVendido,
    int AguardandoResposta,
    int LembretesDeHoje,
    int AutomaticasEnviadas,
    int AutomaticasNaoEnviadas,
    IReadOnlyList<MotivoDeFalha> Motivos,
    int RespostasPesquisa,
    int Promotores,
    int Detratores);

/// <summary>O que o "Reenviar" fez: o dia resumido e para quantos donos o e-mail SAIU.</summary>
public record ResumoReenviado(DateOnly Dia, int Enviados, int Donos);

public interface IServicoResumoDiario
{
    /// <summary>Os numeros de `dia` e os de agora, da empresa do contexto. Roda com tenant: na
    /// rodada diaria, o job assume a empresa como dono (ver `ContextoDeFundo`).</summary>
    Task<ResumoDiario> MontarAsync(DateOnly dia, CancellationToken ct);
}
