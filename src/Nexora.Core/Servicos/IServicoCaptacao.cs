namespace Nexora.Core.Servicos;

/// <summary>===================== O RESUMO DA CAPTAÇÃO, PRONTO (AUD-XX) =====================
/// O topo da tela de Captação: quantos leads cada caminho trouxe, a fatia de cada um, quantos
/// canais e formulários estão ativos, e o aviso de anúncio se perdendo.
///
/// A tela buscava as duas listas e o resumo de anúncios e fazia todas as contas: somava os leads
/// dos formulários, contava os ativos, dividia para a fatia e decidia quando mostrar o aviso.
///
/// `PercentualCanais` e `PercentualFormularios` somam 100 (`Percentual.Fatias`), e são nulos sem
/// lead nenhum. `LeadsDeAnuncioSemEnvio` já é zero para quem está enviando à Meta: dizer "você está
/// perdendo 12 leads" a quem conectou seria mentira.
/// ===================================================================================</summary>
public record ResumoCaptacao(
    int LeadsTotal,
    int LeadsCanais,
    int LeadsFormularios,
    decimal? PercentualCanais,
    decimal? PercentualFormularios,
    int CanaisAtivos,
    int TotalCanais,
    int FormulariosAtivos,
    int TotalFormularios,
    int LeadsDeAnuncioSemEnvio);

public interface IServicoCaptacao
{
    Task<ResumoCaptacao> ResumoAsync(CancellationToken ct);
}
