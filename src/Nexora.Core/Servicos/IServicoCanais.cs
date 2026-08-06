namespace Nexora.Core.Servicos;

/// <summary>Um canal de captação, como o painel o vê.
///
/// `Link`, `Texto` e `PodeRemover` vêm do SERVIDOR e não são montados na tela: o link depende do
/// número da conexão (que a tela não tem por que conhecer), o texto é a mesma string que o
/// webhook vai procurar, e só o banco sabe se já chegou lead por aqui.
///
/// `Numero` e `Link` são NULOS juntos, quando a conexão perdeu o pareamento — e aí o link está
/// quebrado, o que a tela precisa dizer em vez de mostrar um `wa.me/` sem telefone.</summary>
public record CanalDto(
    long Id,
    string Nome,
    string Codigo,
    long ConexaoId,
    string ConexaoNome,
    string? Numero,
    string Origem,
    bool Ativo,
    int LeadsRecebidos,
    // `https://wa.me/{numero}?text=...`
    string? Link,
    // O texto EXATO que a pessoa vai enviar. Visível na tela de propósito: quem cria o canal
    // precisa ver a frase que o cliente dele vai mandar, porque é ela que decide se o código
    // sobrevive ao envio.
    string Texto,
    // O nome de arquivo do QR, SEM extensão (`nexora-panfleto-julho-k7m2`). Vem do servidor
    // porque o download no painel é feito por blob — e blob não carrega `Content-Disposition`.
    // Montar o mesmo slug no cliente seria a segunda cópia de uma regra que já existe aqui.
    string NomeArquivo,
    bool PodeRemover,
    string? MotivoNaoRemove,
    DateTime CriadoEm);

/// <summary>A lista + o que a tela precisa para decidir se dá para criar.
///
/// `PodeCriar` é falso quando a empresa não tem NENHUMA conexão com número pareado — gerar canal
/// aí produziria material impresso com link quebrado. Vale avisar antes, não depois da gráfica.
///
/// `LeadsAtribuidos` é a soma dos contadores. É PISO, não total: quem apagou o código antes de
/// enviar entrou como `whatsapp` e não aparece aqui. Ver `CanalCaptacao`.</summary>
public record CanaisDto(
    IReadOnlyList<CanalDto> Itens,
    IReadOnlyList<ConexaoParaCanal> Conexoes,
    bool PodeCriar,
    int LeadsAtribuidos);

/// <summary>As conexões que podem receber um canal: só as que têm número pareado.</summary>
public record ConexaoParaCanal(long Id, string Nome, string Numero);

public record NovoCanal(string Nome, long ConexaoId, string? Origem);

/// <summary>O QR desenhado em SVG, com o nome de arquivo pronto para o download.</summary>
public record QrDoCanal(string NomeArquivo, string Svg);

public interface IServicoCanais
{
    Task<CanaisDto> ListarAsync(CancellationToken ct);

    /// <summary>Cria o canal e sorteia o código. Recusa se a empresa não tem conexão pareada.</summary>
    Task<long> CriarAsync(NovoCanal novo, CancellationToken ct);

    /// <summary>Renomeia e troca a origem/conexão. O CÓDIGO não muda, nunca: ele já está impresso.
    ///
    /// Renomear NÃO reescreve os leads que já vieram — eles registram de onde vieram no dia em que
    /// vieram, e reescrever isso apagaria a história do lead para acertar um rótulo.</summary>
    Task AtualizarAsync(long id, NovoCanal dados, CancellationToken ct);

    /// <summary>Liga e desliga. Desligado NÃO apaga e NÃO quebra o link: o lead continua entrando,
    /// só que como `whatsapp`, sem atribuição. O material impresso segue no mundo.</summary>
    Task AlternarAtivoAsync(long id, bool ativo, CancellationToken ct);

    /// <summary>Remove. Recusa se JÁ VEIO LEAD por aqui: `contatos.origem_detalhe` guarda o nome
    /// do canal como texto, e apagar a linha deixaria o histórico apontando para um canal que
    /// ninguém mais consegue explicar. Desativar é o caminho.</summary>
    Task RemoverAsync(long id, CancellationToken ct);

    /// <summary>O QR em SVG. Nulo para id inexistente ou de outra empresa — o mesmo nulo para os
    /// dois, senão a diferença entre as respostas conta que o canal existe em outro tenant.</summary>
    Task<QrDoCanal?> SvgAsync(long id, CancellationToken ct);

    /// <summary>O QR em PNG.</summary>
    Task<(string NomeArquivo, byte[] Png)?> PngAsync(long id, CancellationToken ct);
}
