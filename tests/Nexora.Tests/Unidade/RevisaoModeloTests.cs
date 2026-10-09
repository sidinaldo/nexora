using Nexora.Core.Entidades;
using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>O QUE A META DECIDIU SOBRE UM TEMPLATE (INT-XX).
///
/// Errar para mais deixa o vendedor escolher um template que a Meta vai recusar no envio; errar para
/// menos esconde um template aprovado.</summary>
public class RevisaoModeloTests
{
    private static ModeloMensagem Enviado() => new()
    {
        Nome = "boas_vindas", Corpo = "Oi {{nome}}!", WabaId = "1", Status = StatusModelo.Enviado
    };

    [Theory]
    [InlineData("APPROVED", StatusModelo.Aprovado)]
    [InlineData("REINSTATED", StatusModelo.Aprovado)]
    [InlineData("PENDING", StatusModelo.Enviado)]
    [InlineData("IN_APPEAL", StatusModelo.Enviado)]
    [InlineData("REJECTED", StatusModelo.Rejeitado)]
    [InlineData("PAUSED", StatusModelo.Rejeitado)]
    [InlineData("DISABLED", StatusModelo.Rejeitado)]
    [InlineData("approved", StatusModelo.Aprovado)]
    public void O_STATUS_DA_META_VIRA_O_DO_NEXORA(string meta, StatusModelo esperado)
    {
        Assert.Equal(esperado, RevisaoModelo.StatusDe(meta));
    }

    /// <summary>`FLAGGED` e aviso de qualidade: o template continua saindo. Nao mexe em nada.</summary>
    [Theory]
    [InlineData("FLAGGED")]
    [InlineData("QUALQUER_COISA")]
    [InlineData(null)]
    public void EVENTO_QUE_NAO_MUDA_A_DISPONIBILIDADE_NAO_MEXE(string? meta)
    {
        var modelo = Enviado();

        Assert.False(RevisaoModelo.Aplicar(modelo, meta, null));
        Assert.Equal(StatusModelo.Enviado, modelo.Status);
    }

    [Fact]
    public void RECUSADO_GUARDA_O_MOTIVO_EM_PORTUGUES_E_APROVADO_O_LIMPA()
    {
        var modelo = Enviado();

        Assert.True(RevisaoModelo.Aplicar(modelo, "REJECTED", "TAG_CONTENT_MISMATCH"));
        Assert.Equal(StatusModelo.Rejeitado, modelo.Status);
        Assert.Contains("categoria não combina", modelo.MotivoRejeicao);

        Assert.True(RevisaoModelo.Aplicar(modelo, "REINSTATED", "NONE"));
        Assert.Equal(StatusModelo.Aprovado, modelo.Status);
        Assert.Null(modelo.MotivoRejeicao);
    }

    /// <summary>O mesmo evento duas vezes (webhook e consulta) nao conta como mudanca.</summary>
    [Fact]
    public void O_MESMO_RESULTADO_DE_NOVO_NAO_E_MUDANCA()
    {
        var modelo = Enviado();

        Assert.True(RevisaoModelo.Aplicar(modelo, "APPROVED", "NONE"));
        Assert.False(RevisaoModelo.Aplicar(modelo, "APPROVED", "NONE"));
    }

    [Theory]
    [InlineData("REJECTED", "INVALID_FORMAT", "Formato inválido")]
    [InlineData("REJECTED", "PROMOTIONAL", "promocional")]
    [InlineData("REJECTED", "NONE", "sem dizer o motivo")]
    [InlineData("REJECTED", "ALGO_NOVO", "A Meta recusou: ALGO_NOVO")]
    [InlineData("PAUSED", null, "pausou")]
    [InlineData("DISABLED", null, "desativou")]
    public void O_MOTIVO_DIZ_O_QUE_ACONTECEU(string status, string? razao, string trecho)
    {
        Assert.Contains(trecho, RevisaoModelo.Motivo(status, razao));
    }
}
