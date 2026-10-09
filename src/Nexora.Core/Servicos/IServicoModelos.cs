namespace Nexora.Core.Servicos;

/// <summary>Um template, como a tela de conexao o mostra. `Corpo` com as variaveis por NOME.</summary>
public record ModeloDto(
    long Id, long ConexaoId, string Nome, string Categoria, string Idioma, string Corpo,
    IReadOnlyList<string> Variaveis, string Status, string? MotivoRejeicao, DateTime AtualizadoEm);

/// <summary>O formulario do template. `Nome` livre: vira o nome da Meta (`boas_vindas`) no servico.
/// `Idioma` nulo = `pt_BR`.</summary>
public record NovoModelo(string Nome, string Categoria, string Corpo, string? Idioma = null);

/// <summary>Um template aprovado, oferecido na conversa com a janela fechada. `Previa` e o texto ja
/// preenchido para AQUELE cliente — o que ele vai ler.</summary>
public record ModeloDaConversa(long Id, string Nome, string Categoria, string Previa);

/// <summary>===================== OS TEMPLATES DA API OFICIAL (INT-XX) =====================
///
/// So o DONO, como a conexao: template e configuracao, e passa pela revisao da Meta.
///
/// O ciclo: RASCUNHO (editavel) → ENVIADO a Meta → APROVADO ou REJEITADO. A decisao chega pelo
/// webhook, pela consulta do verificador de 5 minutos, ou pelo "Atualizar" da tela.
/// ===================================================================================</summary>
public interface IServicoModelos
{
    /// <summary>Os templates da conta (WABA) desta conexao — os criados por qualquer numero dela.</summary>
    Task<IReadOnlyList<ModeloDto>> ListarAsync(long conexaoId, CancellationToken ct);

    /// <summary>Cria um RASCUNHO. Nada vai a Meta ainda.</summary>
    Task<long> CriarAsync(long conexaoId, NovoModelo novo, CancellationToken ct);

    /// <summary>Edita. So o RASCUNHO: depois de enviado, o texto e o que a Meta revisou.</summary>
    Task EditarAsync(long id, NovoModelo novo, CancellationToken ct);

    /// <summary>Apaga do Nexora o rascunho ou o rejeitado. Template ja usado em mensagem fica.</summary>
    Task ExcluirAsync(long id, CancellationToken ct);

    /// <summary>Manda o rascunho para a revisao da Meta.</summary>
    Task<ModeloDto> SubmeterAsync(long id, CancellationToken ct);

    /// <summary>Pergunta a Meta como esta a revisao, agora.</summary>
    Task<ModeloDto> SincronizarAsync(long id, CancellationToken ct);
}
