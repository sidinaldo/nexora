namespace Nexora.Core.Servicos;

/// <summary>Um plano do catálogo, como a tela do operador o vê.
///
/// `EmpresasNoPlano` vem do servidor e não é deduzido na tela: é o número que responde "posso
/// arquivar este plano sem deixar ninguém órfão", e a tela não tem esse dado. Mesma disciplina do
/// `PodeRemover` das conexões.</summary>
public record PlanoDto(
    long Id, string Nome, decimal Preco, short LimiteConexoes, short LimiteUsuarios,
    bool Ativo, short Ordem, int EmpresasNoPlano);

public record NovoPlano(string Nome, decimal Preco, short LimiteConexoes, short LimiteUsuarios, short Ordem);

/// <summary>⚠️ EDITAR UM PLANO NÃO MEXE EM EMPRESA NENHUMA. Os limites já atribuídos foram COPIADOS
/// para a linha de cada empresa; este registro é o molde para as próximas atribuições. Ver
/// <see cref="Nexora.Core.Entidades.Plano"/>.</summary>
public record EditarPlano(
    string Nome, decimal Preco, short LimiteConexoes, short LimiteUsuarios, bool Ativo, short Ordem);

/// <summary>Ajuste manual dos tetos de uma empresa, sem tirá-la do plano — o caso "esse cliente
/// negociou uma conexão a mais".
///
/// ⚠️ `ConfirmarExcedente` existe porque baixar um teto abaixo do uso atual é LEGAL e quase sempre
/// não é o que a pessoa quis. Sem a confirmação o serviço recusa e explica que ninguém perde acesso
/// — a confirmação é o que transforma "digitei errado" em "estou registrando um downgrade".</summary>
public record AjusteDeLimites(
    short LimiteConexoes, short LimiteUsuarios, bool ConfirmarExcedente = false);

/// <summary>O que uma empresa usa hoje, ao lado do que ela pode. Vai em TODA resposta de escrita:
/// sem o uso ao lado do limite, o operador não enxerga que acabou de criar uma empresa acima da
/// cota, e quem descobre é o dono dela, no convite seguinte.</summary>
public record LimitesDaEmpresa(
    long EmpresaId, string Nome, bool Ativa,
    long? PlanoId, string? PlanoNome,
    short LimiteConexoes, int ConexoesUsadas,
    short LimiteUsuarios, int VagasUsadas);

/// <summary>Uma empresa cliente na lista do operador. NÚMEROS, e só.
///
/// ===================== O QUE NÃO ESTÁ AQUI, E É DELIBERADO =====================
/// Nenhum nome de contato, nenhum telefone, nenhum texto de mensagem, nenhum e-mail da equipe. A
/// área do operador existe para responder "como vai este cliente", não para ler a conversa dele.
///
/// O e-mail do DONO fica de fora da lista e aparece só no detalhe de uma empresa: cinquenta
/// endereços numa tabela é um diretório de pessoas; um endereço na tela onde se vai mudar o plano
/// é o contexto da ligação que se vai fazer. Exposições diferentes, respostas diferentes.
/// ==============================================================================</summary>
public record EmpresaNaLista(
    long Id, string Nome, bool Ativa, bool Demonstracao,
    long? PlanoId, string? PlanoNome,
    short LimiteConexoes, short LimiteUsuarios,
    /// <summary>Os limites fugiram do molde — alguém ajustou esta empresa à mão.</summary>
    bool LimitesPersonalizados,
    DateTime CriadaEm,
    /// <summary>⚠️ A MÉTRICA QUE PREVÊ ABANDONO MELHOR QUE QUALQUER OUTRA, nas palavras do próprio
    /// `Empresa.PrimeiraMensagemEm`: o intervalo entre a empresa assinar e o produto funcionar.
    /// Nula enquanto a primeira mensagem não chegou — e empresa parada aí é empresa que nunca
    /// pareou o WhatsApp.</summary>
    double? HorasAteValor,
    int UsuariosAtivos, int UsuariosConvidados, int VagasUsadas,
    int Conexoes, int ConexoesConectadas,
    int Contatos,
    DateTime? UltimoAcessoEm, DateTime? UltimaMensagemEm,
    int NegociacoesAbertas, int GanhasNaJanela, decimal ValorGanhoNaJanela);

/// <summary>`Dias` recorta as métricas de movimento (mensagens, vendas); as de estado (vagas,
/// conexões, contatos) são sempre o agora.</summary>
public record FiltroEmpresas(string? Busca = null, int Pagina = 1, int Tamanho = 25, int Dias = 30);

/// <summary>===================== A ÁREA DO OPERADOR (OPE-1) =====================
///
/// Quem usa isto é o operador do produto, nunca um cliente. Não há papel novo, não há claim novo e
/// não há usuário: a credencial é a chave de administração no cabeçalho, a mesma do cadastro de
/// empresa, e quem a confere é o controller.
///
/// ⚠️ TODA IMPLEMENTAÇÃO DESTA INTERFACE RODA SEM SESSÃO, e precisa recusar o contrário. Alcançada
/// de dentro de uma requisição autenticada, ela trocaria o tenant daquela requisição no meio do
/// caminho — que é o único modo de falha que este sistema inteiro foi desenhado para não ter.
/// =========================================================================</summary>
public interface IServicoOperador
{
    // ---- o catálogo ----
    Task<IReadOnlyList<PlanoDto>> ListarPlanosAsync(CancellationToken ct);
    Task<long> CriarPlanoAsync(NovoPlano novo, CancellationToken ct);
    Task AtualizarPlanoAsync(long planoId, EditarPlano dados, CancellationToken ct);

    // ---- os números ----
    Task<PaginaComTotal<EmpresaNaLista>> ListarEmpresasAsync(FiltroEmpresas filtro, CancellationToken ct);

    // ---- a empresa ----
    Task<LimitesDaEmpresa> LimitesAsync(long empresaId, CancellationToken ct);

    /// <summary>Atribui um plano COPIANDO os limites dele para a empresa. O plano vira rótulo; quem
    /// manda daqui para frente são os limites da linha da empresa.</summary>
    Task<LimitesDaEmpresa> AtribuirPlanoAsync(long empresaId, long planoId, bool confirmarExcedente, CancellationToken ct);

    Task<LimitesDaEmpresa> AjustarLimitesAsync(long empresaId, AjusteDeLimites ajuste, CancellationToken ct);

    /// <summary>⚠️ DESATIVAR BLOQUEIA LOGIN NOVO, não derruba quem já está dentro — o token dura 12h
    /// e não é reconferido por requisição. A tela precisa dizer isso em palavras: um botão
    /// "desativar" que não desconecta ninguém é uma mentira que se descobre no meio de um
    /// incidente.</summary>
    Task<LimitesDaEmpresa> DefinirAtivaAsync(long empresaId, bool ativa, CancellationToken ct);
}
