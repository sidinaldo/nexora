using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Webhooks;
using Nexora.Infra.Persistencia;
using Nexora.Infra.Webhooks;

namespace Nexora.Tests.Integracao;

/// <summary>O publicador de eventos DE VERDADE, montado para os testes que não são sobre webhook.
///
/// ===================== POR QUE NÃO UM FAKE =====================
/// Um fake aqui esconderia exatamente o que mais importa: o publicador consulta
/// `webhooks_saida` com `IgnoreQueryFilters`, e é essa consulta que decide se o evento sai. Com um
/// dublê, um `ServicoContatos` que publicasse errado continuaria passando em todo teste de
/// contato — e a falha só apareceria em produção, como "metade dos leads não chega no ERP".
///
/// Com o real, o custo é uma consulta a mais por escrita nos testes existentes. Sem webhook
/// configurado ela devolve nulo e o publicador volta na hora, que é o caminho de toda empresa que
/// não usa a integração.
/// ==============================================================</summary>
public static class PublicadorDeTeste
{
    public static IPublicadorEventos Novo(NexoraDbContext db, TimeProvider? relogio = null) =>
        new PublicadorEventos(
            db, relogio ?? TimeProvider.System, NullLogger<PublicadorEventos>.Instance);
}
