using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Nps;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Servicos;

namespace Nexora.Tests.Integracao;

/// <summary>A leitura da nota de NPS DE VERDADE, montada para os testes que nao sao sobre NPS.
///
/// ===================== POR QUE NAO UM DUBLE =====================
/// Mesma razao do `PublicadorDeTeste` ao lado. Esta leitura roda no caminho QUENTE de toda mensagem
/// recebida, e decide se a entrada acende o semaforo. Com um duble, um dia em que ela passasse a
/// suprimir o semaforo de mensagem comum continuaria verde em todo teste de webhook — e a falha
/// apareceria em producao como "o vendedor nao ve as perguntas dos clientes".
///
/// Com a real, o custo e UMA consulta a mais por mensagem recebida nos testes existentes: sem
/// pesquisa aberta ela sai antes de olhar o texto, pelo indice parcial
/// `ix_pesquisas_nps_aberta`. E o caminho de toda empresa que nao usa a pesquisa.
/// ================================================================</summary>
public static class LeituraNpsDeTeste
{
    public static ILeituraDaResposta Novo(
        NexoraDbContext db, TimeProvider? relogio = null, IClienteWhatsApp? cliente = null)
    {
        var tempo = relogio ?? TimeProvider.System;

        var enviador = new EnviadorMensagem(
            new DadosMensagem(db, tempo), cliente ?? new ClienteWhatsAppFalso(),
            new OpcoesEnvio { IntervaloEntreEnvios = TimeSpan.Zero },
            tempo, NullLogger<EnviadorMensagem>.Instance);

        return new LeituraDaResposta(
            db,
            new AcoesDaNota(db, enviador, tempo, NullLogger<AcoesDaNota>.Instance),
            tempo);
    }
}
