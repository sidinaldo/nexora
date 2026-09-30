using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Conversoes;
using Nexora.Infra.Conversoes;
using Nexora.Infra.Persistencia;

namespace Nexora.Tests.Integracao;

/// <summary>O publicador de conversões DE VERDADE, montado para os testes que não são sobre
/// conversão (INT-4).
///
/// ===================== POR QUE NÃO UM FAKE =====================
/// Mesma razão do `PublicadorDeTeste`: o que mais importa aqui é a consulta que decide se o evento
/// sai — `credenciais_conversao` com `IgnoreQueryFilters`, porque dois dos três chamadores rodam
/// sem tenant. Com um dublê, um caminho que publicasse errado continuaria passando em todo teste de
/// contato, e a falha apareceria só em produção: "as vendas do site não chegam na Meta".
///
/// Sem credencial configurada ele devolve na hora — que é o caminho de toda empresa que não usa a
/// integração, ou seja, de praticamente todo teste deste projeto.
/// ==============================================================</summary>
public static class PublicadorConversoesDeTeste
{
    /// <summary>O segredo do `external_id` nos testes. Fixo de propósito: o valor do hash entra em
    /// asserção, e um segredo aleatório por execução tornaria o teste irreprodutível.
    ///
    /// ⚠️ EM PRODUÇÃO ELE NASCE DE `openssl rand -hex 32` E NUNCA MUDA — trocá-lo troca todos os
    /// `external_id` e a Meta perde o elo entre o `Lead` e a `Compra` da mesma pessoa.</summary>
    public const string Segredo = "segredo-de-teste-do-external-id-com-mais-de-32-caracteres";

    public static OpcoesConversoes Opcoes => new() { SegredoExternalId = Segredo };

    public static IPublicadorConversoes Novo(NexoraDbContext db, TimeProvider? relogio = null) =>
        new PublicadorConversoes(
            db, relogio ?? TimeProvider.System, Opcoes,
            NullLogger<PublicadorConversoes>.Instance);
}
