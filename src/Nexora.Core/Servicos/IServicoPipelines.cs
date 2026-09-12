namespace Nexora.Core.Servicos;

/// <summary>Uma pipeline na lista.
///
/// ⚠️ SEM CONTAGEM DE CONTATOS, e é uma decisão, não esquecimento. O menu mostrando "Vendas · 47"
/// exigiria uma quarta cópia da regra de visibilidade do card — `RegrasContato.NoQuadro` mais a
/// regra de venda em aberto da etapa de ganho, que hoje já vive em três lugares (a `Expression`
/// compartilhada, o `||` inline do `ServicoFunil` e do `ServicoDashboard`, e o SQL longo do
/// relatório 4).
///
/// Essa regra já divergiu uma vez, e o cliente viu "o dashboard dizia 72 em Proposta e o quadro
/// tinha 69 cards" — que é o pior tipo de bug num produto que vende controle de dados. Um número
/// no menu que discorda do quadro seria o mesmo defeito num lugar mais visível. Quando a contagem
/// entrar, ela entra pela mesma expressão que o quadro usa, não por uma cópia nova.</summary>
public record PipelineDto(long Id, string Nome, string Cor, short Ordem, bool Padrao, int Etapas);

public record NovaPipeline(string Nome, string? Cor);

public record EditarPipeline(string Nome, string? Cor);

/// <summary>Os funis da empresa.
///
/// ===================== QUEM LÊ E QUEM ESCREVE SÃO DIFERENTES =====================
/// `ListarAsync` é de QUALQUER papel — é o menu, e o vendedor precisa navegar entre os quadros.
/// O resto é só do dono: criar uma pipeline é definir um processo de trabalho para a empresa
/// inteira, e não uma escolha de quem está atendendo.
///
/// Mesma separação de `EtiquetasController`, e pelo mesmo motivo.
/// ================================================================================</summary>
public interface IServicoPipelines
{
    /// <summary>Ordenada por `Ordem`, depois nome. A padrão NÃO vem primeiro à força: quem
    /// trabalha o dia inteiro em "Atacado" quer ela em cima, e o menu é dele.</summary>
    Task<IReadOnlyList<PipelineDto>> ListarAsync(CancellationToken ct);

    /// <summary>O id da pipeline padrão — onde entra quem não escolheu funil nenhum.
    ///
    /// Usada por quem tem contexto de tenant — hoje o quadro aberto sem pipeline escolhida.
    ///
    /// ⚠️ A CAPTURA DE FORMULÁRIO E O WEBHOOK DO WHATSAPP NÃO usam este método, e não é
    /// descuido: os dois rodam FORA de uma requisição autenticada, com o `empresaId` resolvido do
    /// `instance_name` da conexão, e consultam com `IgnoreQueryFilters`. Um serviço que depende de
    /// `IContextoEmpresa` não serve ali. Eles repetem a ordenação (`padrao`, depois `ordem`,
    /// depois `id`) — e essa repetição é dívida conhecida, não acidente: quando o código de
    /// campanha passar a escolher a pipeline, os três caminhos se encontram num lugar só.</summary>
    Task<long> PadraoAsync(CancellationToken ct);

    /// <summary>Cria com duas etapas — uma de entrada e uma de ganho.
    ///
    /// ⚠️ NÃO existe pipeline sem etapa. O lead entra na etapa de MENOR ordem e o quadro não
    /// desenha sem coluna; uma pipeline vazia seria um item de menu que leva a uma tela quebrada.
    /// Mesma razão pela qual o cadastro da empresa semeia cinco etapas na mesma transação.</summary>
    Task<long> CriarAsync(NovaPipeline nova, CancellationToken ct);

    Task AtualizarAsync(long id, EditarPipeline dados, CancellationToken ct);

    /// <summary>Apaga a pipeline E as etapas dela, na mesma transação.
    ///
    /// Recusa em dois casos, e os dois protegem dado do cliente:
    ///   • é a pipeline PADRÃO — é por onde o lead entra, e sem ela o próximo lead não teria
    ///     destino. Marque outra como padrão primeiro.
    ///   • tem CONTATO em alguma etapa dela — apagar levaria os contatos junto, e contato é o
    ///     ativo do cliente. Mesma disciplina de `etapas_funil`, que é `ON DELETE RESTRICT` e
    ///     exige destino.</summary>
    Task RemoverAsync(long id, CancellationToken ct);

    /// <summary>Move a marca de padrão. Operação própria, como `DefinirGanhoAsync` nas etapas:
    /// muda para onde todo lead novo vai, e merece um clique só dela.</summary>
    Task DefinirPadraoAsync(long id, CancellationToken ct);
}
