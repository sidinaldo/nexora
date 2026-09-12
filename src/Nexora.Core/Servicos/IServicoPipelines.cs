namespace Nexora.Core.Servicos;

/// <summary>Uma pipeline na lista do menu.
///
/// ===================== A CONTAGEM, E POR QUE ELA QUASE NAO EXISTIU =====================
/// `Contatos` e quantos negocios abertos a pipeline tem — o numero que aparece ao lado do nome
/// no menu.
///
/// Eu tinha DEIXADO ELE DE FORA num primeiro momento, com um argumento que continua valendo: a
/// regra de "o que aparece no quadro" ja vive em tres lugares (a `Expression` compartilhada, o
/// `||` inline do `ServicoFunil` e do `ServicoDashboard`, e o SQL longo do relatorio 4). Ela ja
/// divergiu uma vez, e o cliente viu o dashboard dizer 72 numa etapa onde o quadro tinha 69 — o
/// pior tipo de defeito num produto que vende controle de dados.
///
/// O que estava errado era a CONCLUSAO. Nao mostrar o numero nao elimina o risco; adia. A
/// protecao de verdade nao e "nao duplicar", e "provar que batem": `PipelinesDbTests` exige que
/// esta contagem seja igual a SOMA das colunas que `ServicoFunil.QuadroAsync` devolve para a
/// mesma pipeline. Se alguem mexer numa das duas, o teste cai.
/// ====================================================================================</summary>
public record PipelineDto(
    long Id, string Nome, string Cor, short Ordem, bool Padrao, int Etapas, int Contatos);

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
