namespace Nexora.Core.Entidades;

/// <summary>O resumo de UM dia de UMA empresa ja saiu (RES-XX). Unico por (empresa, dia): a reserva
/// e um INSERT que so acerta uma vez, e um reinicio da API perto das 8h nao manda o mesmo e-mail
/// duas vezes.
///
/// So a marca: o que foi enviado — e se chegou — esta em `emails_enviados`.</summary>
public class ResumoDiarioEnviado
{
    public long Id { get; set; }
    public long EmpresaId { get; set; }

    /// <summary>O dia RESUMIDO (ontem, no fuso da empresa), e nao o do envio.</summary>
    public DateOnly Dia { get; set; }

    public DateTime CriadoEm { get; set; }
}
