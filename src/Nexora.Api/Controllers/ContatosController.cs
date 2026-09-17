using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Core.Servicos;

namespace Nexora.Api.Controllers;

/// <summary>Os contatos. Qualquer papel: cadastrar e trabalhar lead é atendimento, não
/// configuração — o vendedor precisa disso o dia inteiro.</summary>
[ApiController]
[Route("api/contatos")]
[Authorize]
public class ContatosController(
    IServicoContatos servico,
    IServicoImportacao importacao) : ControllerBase
{
    // ==================================================================== importar (issue #8)
    /// <summary>===================== DOIS PASSOS, E O ARQUIVO SOBE NOS DOIS =====================
    /// `previa` lê e julga sem gravar; `importar` grava. O arquivo sobe uma vez em cada, e é de
    /// propósito: guardá-lo entre os dois pedidos exigiria estado de servidor com dono, prazo e
    /// limpeza — para economizar um upload de no máximo 1 MB.
    ///
    /// ⚠️ `RequestSizeLimit` COM FOLGA, pelo mesmo motivo de `EnviarMidia`: o teto do serviço é do
    /// CONTEÚDO, e o multipart carrega envelope por cima (limites de borda, nome do arquivo, o
    /// campo do funil). Sem a folga o servidor cortaria antes, e a recusa viria como erro de
    /// protocolo em vez da mensagem que diz para dividir a planilha.
    ///
    /// A PRÉVIA É DE QUALQUER PAPEL — ela não grava nada, e o vendedor que recebeu a planilha
    /// precisa poder conferir antes de pedir o import. Quem grava é dono ou gestor, e a recusa
    /// vem do serviço.
    /// ================================================================================</summary>
    [HttpPost("importacao/previa")]
    [RequestSizeLimit(IServicoImportacao.MaximoBytes + 256 * 1024)]
    public async Task<IActionResult> Previa(IFormFile arquivo, CancellationToken ct) =>
        Ok(await importacao.PreverAsync(await BytesAsync(arquivo, ct), ct));

    /// <summary>`pipelineId` nulo — o padrão — cria só os contatos. Com funil, abre também uma
    /// negociação na primeira etapa dele. Ver `IServicoImportacao` para por que o padrão é esse.</summary>
    [HttpPost("importacao")]
    [RequestSizeLimit(IServicoImportacao.MaximoBytes + 256 * 1024)]
    public async Task<IActionResult> Importar(
        IFormFile arquivo, [FromForm] long? pipelineId, CancellationToken ct) =>
        Ok(await importacao.ImportarAsync(await BytesAsync(arquivo, ct), pipelineId, ct));

    /// <summary>⚠️ O ARQUIVO INTEIRO NA MEMÓRIA, e é uma escolha: 1 MB por pedido, e o parser
    /// precisa do texto todo porque um campo entre aspas pode conter quebra de linha — ler em
    /// fluxo exigiria a mesma máquina de estados com mais partes móveis, pelo mesmo resultado.</summary>
    private static async Task<byte[]> BytesAsync(IFormFile? arquivo, CancellationToken ct)
    {
        if (arquivo is null || arquivo.Length == 0)
            throw new RegraDeNegocioException("Escolha um arquivo .csv.");

        using var memoria = new MemoryStream();
        await arquivo.CopyToAsync(memoria, ct);
        return memoria.ToArray();
    }

    [HttpGet]
    public async Task<IActionResult> Listar(
        [FromQuery] FiltroContato filtro = FiltroContato.Abertos,
        [FromQuery] string? busca = null,
        [FromQuery] long? etapaId = null,
        [FromQuery] long? responsavelId = null,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanho = 30,
        CancellationToken ct = default) =>
        Ok(await servico.ListarAsync(filtro, busca, etapaId, responsavelId, pagina, tamanho, ct));

    [HttpGet("{id:long}")]
    public async Task<IActionResult> Detalhe(long id, CancellationToken ct) =>
        Ok(await servico.DetalheAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Criar([FromBody] NovoContato novo, CancellationToken ct) =>
        Ok(new { id = await servico.CriarAsync(novo, ct) });

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Atualizar(
        long id, [FromBody] EditarContato dados, CancellationToken ct)
    {
        await servico.AtualizarAsync(id, dados, ct);
        return NoContent();
    }

    /// <summary>A PORTA ÚNICA DO GANHO. Arrastar o card para a coluna de venda e clicar em "venda
    /// fechada" chamam este mesmo endpoint — `POST /api/funil/{id}/mover` recusa a etapa de ganho
    /// justamente para forçar por aqui, onde o valor é obrigatório.</summary>
    [HttpPost("{id:long}/ganho")]
    public async Task<IActionResult> MarcarGanho(
        long id, [FromBody] RegistrarGanho corpo, CancellationToken ct)
    {
        await servico.MarcarGanhoAsync(id, corpo.Valor, corpo.CanalId, corpo.NegociacaoId, ct);
        return NoContent();
    }

    /// <summary>O que o modal de fechamento precisa para oferecer o canal (NEG-3). Endpoint
    /// próprio e não um campo no detalhe do contato: o funil abre o mesmo modal a partir de um
    /// card, que não carrega o detalhe — e engordar o payload do kanban por causa de um campo
    /// que só aparece num modal seria pagar em toda rolagem do quadro.</summary>
    [HttpGet("{id:long}/canais-fechamento")]
    public async Task<IActionResult> CanaisDoFechamento(long id, CancellationToken ct) =>
        Ok(await servico.CanaisDoFechamentoAsync(id, ct));

    [HttpPost("{id:long}/perda")]
    public async Task<IActionResult> MarcarPerdido(
        long id, [FromBody] RegistrarPerda corpo, CancellationToken ct)
    {
        await servico.MarcarPerdidoAsync(id, corpo.Motivo, corpo.NegociacaoId, ct);
        return NoContent();
    }

    /// <summary>Começa um negócio com este contato (E6).
    ///
    /// ⚠️ ERA `POST /{id}/reabrir`, E O NOME ANTIGO ERA METADE DA HISTÓRIA. "Reabrir" descreve o
    /// contato que já teve negócio; o lead que acabou de chegar pela caixa nunca teve nenhum, e é
    /// o caso mais comum desde o E6. O gesto é o mesmo — o serviço decide se revive a perda ou
    /// abre linha nova.
    ///
    /// QUALQUER PAPEL: decidir que uma conversa virou negócio é o trabalho do dia do vendedor,
    /// não configuração. Mesma assimetria que criar etiqueta (dono) e aplicar etiqueta (todos).</summary>
    [HttpPost("{id:long}/negociacao")]
    public async Task<IActionResult> AbrirNegociacao(
        long id, [FromBody] AbrirNegociacao? corpo, CancellationToken ct)
    {
        // Corpo AUSENTE é válido e significa "escolha por mim": a caixa manda o funil escolhido,
        // e a tela do contato do cliente recorrente costuma não mandar nada.
        await servico.AbrirNegociacaoAsync(id, corpo?.PipelineId, ct);
        return NoContent();
    }

    /// <summary>LGPD. Só DONO e GESTOR: apagar a PII é irreversível, e o histórico do contato
    /// deixa de ter nome para sempre.</summary>
    [HttpPost("{id:long}/anonimizar")]
    [Authorize(Roles = "dono,gestor")]
    public async Task<IActionResult> Anonimizar(long id, CancellationToken ct)
    {
        await servico.AnonimizarAsync(id, ct);
        return NoContent();
    }
}

/// <summary>`CanalId` é OPCIONAL e omiti-lo é o caso normal (NEG-3): sem ele a venda herda o canal
/// do ciclo detectado nas mensagens. Informar serve para o vendedor confirmar ou corrigir — é o
/// único ponto onde alguém sabe de verdade por que o cliente voltou.</summary>
public record RegistrarGanho(decimal Valor, long? CanalId = null, long? NegociacaoId = null);
/// <summary>Corpo de `POST /contatos/{id}/negociacao`. `PipelineId` nulo deixa o servidor
/// escolher — ver `IServicoContatos.AbrirNegociacaoAsync` para a precedência.</summary>
public record AbrirNegociacao(long? PipelineId);

public record RegistrarPerda(string Motivo, long? NegociacaoId = null);
