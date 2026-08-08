using Microsoft.EntityFrameworkCore;
using Nexora.Core;
using Nexora.Core.Auditoria;
using Nexora.Core.Entidades;
using Nexora.Core.Servicos;
using Nexora.Core.Whatsapp;
using Nexora.Infra.Persistencia;

namespace Nexora.Infra.Servicos;

/// <summary>O caminho QUENTE: o vendedor respondendo na caixa de entrada o dia inteiro.
///
/// Roda DENTRO de requisicao autenticada, entao o query filter global vale e nao ha
/// IgnoreQueryFilters aqui — nem deve haver. Pedir uma conversa de outro tenant simplesmente
/// nao encontra.</summary>
public class ServicoConversas(
    NexoraDbContext db,
    IContextoEmpresa contexto,
    EnviadorMensagem enviador,
    IArmazenamentoMidia armazenamento,
    ColetorAuditoria trilha,
    TimeProvider relogio) : IServicoConversas
{

    public async Task<RespostaEnviada> ResponderAsync(long conversaId, string texto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(texto))
            throw new RegraDeNegocioException("Escreva uma mensagem antes de enviar.");

        var conversa = await db.Conversas
            .Include(c => c.Contato)
            .Include(c => c.Conexao)
            .FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        if (string.IsNullOrWhiteSpace(conversa.Contato.Telefone))
            throw new RegraDeNegocioException("Este contato não tem telefone — não dá para responder.");

        // FREIO POR CONEXAO. Sem o numero pareado, postar so empilha erro; melhor recusar com
        // mensagem clara para o vendedor reconectar. Diferente do lembrete automatico, aqui NAO
        // se reserva para depois: ele esta olhando a tela e precisa saber agora.
        if (!await enviador.InstanciaConectadaAsync(conversa.Conexao.InstanceName, ct))
            throw new RegraDeNegocioException(
                "O WhatsApp está desconectado. Reconecte o número em Conexão e tente de novo.",
                conflito: true);

        var agora = relogio.GetUtcNow().UtcDateTime;

        // Transacao propria so quando nao ha uma em curso (abrir aninhada lanca). A mensagem e a
        // atualizacao da conversa tem que cair juntas: se a mensagem sai e o aguardando_desde
        // nao zera, o semaforo continua vermelho para uma conversa ja respondida.
        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            // ATRIBUICAO, NAO FILA: responder conversa sem dono ATRIBUI ao vendedor. E a defesa
            // contra dois responderem por cima. Se ja tem dono (mesmo outro), responder nao
            // rouba — quem quiser assumir usa AssumirAsync, que devolve 409.
            conversa.ResponsavelId ??= UsuarioAtual();
            conversa.AtribuidoEm ??= agora;

            var mensagem = new Mensagem
            {
                EmpresaId = conversa.EmpresaId,
                ConversaId = conversa.Id,
                ContatoId = conversa.ContatoId,
                ConexaoId = conversa.ConexaoId,
                InstanceName = conversa.Conexao.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                Texto = texto,
                TipoMidia = TipoMidia.Nenhum,
                // lembrete_id NULL: mensagem manual NAO entra no teto diario nem no dedupe por
                // lembrete. O vendedor responde quantas vezes precisar.
                LembreteId = null,
                EnviadoPor = UsuarioAtual(),
                // data_disparo e obrigatorio em saida (ck_msg_data_disparo). Para manual e o
                // proprio dia: nao ha reserve-defer aqui.
                DataDisparo = DateOnly.FromDateTime(agora),
                ReservadoEm = agora
            };

            var (mensagemId, resultado) = await enviador.EnviarManualAsync(
                mensagem, conversa.Contato.Telefone, ct);

            // A conversa e atualizada mesmo se o POST falhou: a mensagem EXISTE, aparece na
            // thread, e do ponto de vista de "quem esta esperando resposta" nos ja respondemos.
            conversa.AguardandoDesde = null;
            conversa.NaoLidas = 0;
            conversa.UltimaMensagemEm = agora;
            conversa.UltimaMensagemDirecao = DirecaoMensagem.Saida;
            conversa.UltimaMensagemPrevia = PreviaTexto.Cortar(texto);

            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);

            var erro = resultado == ResultadoEnvio.Enviada
                ? null
                : await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                    .Where(m => m.Id == mensagemId).Select(m => m.Erro).FirstOrDefaultAsync(ct);

            return new RespostaEnviada(mensagemId, resultado == ResultadoEnvio.Enviada, erro);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== midia (MID-1)
    public async Task<RespostaEnviada> EnviarMidiaAsync(
        long conversaId, ArquivoParaEnvio arquivo, string? legenda, CancellationToken ct)
    {
        // ===================== O CONTEUDO MANDA, NAO A EXTENSAO =====================
        // `NomeArquivo` e `MimeDeclarado` vem do navegador; os dois sao texto que o cliente
        // escolhe. Um `.pdf` renomeado passaria por qualquer checagem de extensao.
        //
        // O mime usado daqui para frente e o DETECTADO. Se os bytes nao baterem com nenhum
        // formato que aceitamos, o arquivo e recusado antes de tocar no disco.
        // ============================================================================
        if (arquivo.Conteudo.Length == 0)
            throw new RegraDeNegocioException("O arquivo está vazio.");

        if (!ValidadorMidia.TamanhoOk(arquivo.Conteudo.LongLength))
            throw new RegraDeNegocioException(
                $"O arquivo tem {arquivo.Conteudo.LongLength / (1024 * 1024)} MB. " +
                $"O limite do WhatsApp é {ValidadorMidia.TamanhoMaximoBytes / (1024 * 1024)} MB.");

        var mime = AssinaturaArquivo.Detectar(arquivo.Conteudo)
            ?? throw new RegraDeNegocioException(
                "Não reconhecemos este arquivo. Envie uma imagem (JPG, PNG ou WEBP) ou um PDF.");

        if (!ValidadorMidia.PermitidoParaEnvio(mime))
            throw new RegraDeNegocioException(
                "Por enquanto dá para enviar só imagem (JPG, PNG ou WEBP) e PDF.");

        var conversa = await CarregarParaEnvioAsync(conversaId, ct);
        var agora = relogio.GetUtcNow().UtcDateTime;

        // GUARDA ANTES DE GRAVAR A LINHA: se o disco falhar, nao existe mensagem apontando para
        // arquivo que nao esta la. A ordem inversa produziria anexo quebrado na thread.
        var chave = $"emp-{conversa.EmpresaId}/saida-{Guid.NewGuid():N}.{ValidadorMidia.ExtensaoDe(mime)}";
        await armazenamento.SalvarAsync(arquivo.Conteudo, chave, ct);

        var nome = NomeSeguro(arquivo.NomeArquivo, mime);

        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            conversa.ResponsavelId ??= UsuarioAtual();
            conversa.AtribuidoEm ??= agora;

            var mensagem = new Mensagem
            {
                EmpresaId = conversa.EmpresaId,
                ConversaId = conversa.Id,
                ContatoId = conversa.ContatoId,
                ConexaoId = conversa.ConexaoId,
                InstanceName = conversa.Conexao.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                // A LEGENDA vai na coluna `texto`, igual ao recebimento: e o mesmo campo que a
                // thread exibe abaixo do anexo, e separar os dois faria a mesma informacao ter
                // dois lugares dependendo de quem mandou.
                Texto = string.IsNullOrWhiteSpace(legenda) ? null : legenda.Trim(),
                TipoMidia = ValidadorMidia.TipoDe(mime),
                MidiaChave = chave,
                MidiaMime = mime,
                MidiaNome = nome,
                MidiaBytes = arquivo.Conteudo.Length,
                LembreteId = null,
                EnviadoPor = UsuarioAtual(),
                DataDisparo = DateOnly.FromDateTime(agora),
                ReservadoEm = agora
            };

            var (mensagemId, resultado) = await enviador.EnviarMidiaManualAsync(
                mensagem, conversa.Contato.Telefone,
                Convert.ToBase64String(arquivo.Conteudo), mime, nome, mensagem.Texto, ct);

            AtualizarConversaComSaida(conversa, mensagem.Texto ?? PreviaDeAnexo(mime, nome), agora);

            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);

            return await MontarRespostaAsync(mensagemId, resultado, ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    // ==================================================================== audio (bloco 13)
    public async Task<RespostaEnviada> EnviarAudioAsync(
        long conversaId, ArquivoParaEnvio arquivo, CancellationToken ct)
    {
        if (arquivo.Conteudo.Length == 0)
            throw new RegraDeNegocioException("A gravação saiu vazia. Tente de novo.");

        // O MESMO teto de tamanho da outra mídia — não um número novo.
        if (!ValidadorMidia.TamanhoOk(arquivo.Conteudo.LongLength))
            throw new RegraDeNegocioException(
                $"O áudio passa de {ValidadorMidia.TamanhoMaximoBytes / (1024 * 1024)} MB.");

        // ===================== O FORMATO É A REGRA DESTE BLOCO =====================
        // OGG passa direto (Firefox), WebM/Opus é REEMPACOTADO sem recodificar (Chrome), e o
        // resto — MP4/AAC do Safari — é recusado. Mandar o formato errado não dá erro: chega
        // como ARQUIVO ANEXO em vez de nota de voz, e ninguém percebe.
        // ==========================================================================
        var ogg = AudioOpus.ParaNotaDeVoz(arquivo.Conteudo)
            ?? throw new RegraDeNegocioException(
                "Este navegador não grava no formato que o WhatsApp usa para áudio. " +
                "Use o Chrome, ou grave pelo celular.");

        var duracao = AudioOpus.DuracaoDe(ogg)
            ?? throw new RegraDeNegocioException("Não foi possível ler a gravação. Tente de novo.");

        if (duracao > AudioOpus.DuracaoMaxima)
            throw new RegraDeNegocioException(
                $"O áudio tem {(int)duracao.TotalSeconds}s. O limite é " +
                $"{(int)AudioOpus.DuracaoMaxima.TotalMinutes} minutos.");

        if (duracao < TimeSpan.FromSeconds(1))
            throw new RegraDeNegocioException("A gravação ficou curta demais.");

        var conversa = await CarregarParaEnvioAsync(conversaId, ct);
        var agora = relogio.GetUtcNow().UtcDateTime;

        var chave = $"emp-{conversa.EmpresaId}/voz-{Guid.NewGuid():N}.ogg";
        await armazenamento.SalvarAsync(ogg, chave, ct);

        var nome = $"audio-{agora:yyyyMMdd-HHmmss}.ogg";
        var segundos = (int)Math.Round(duracao.TotalSeconds);

        var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        try
        {
            conversa.ResponsavelId ??= UsuarioAtual();
            conversa.AtribuidoEm ??= agora;

            var mensagem = new Mensagem
            {
                EmpresaId = conversa.EmpresaId,
                ConversaId = conversa.Id,
                ContatoId = conversa.ContatoId,
                ConexaoId = conversa.ConexaoId,
                InstanceName = conversa.Conexao.InstanceName,
                Direcao = DirecaoMensagem.Saida,
                // SEM legenda: nota de voz não tem. Texto junto viraria uma segunda mensagem no
                // WhatsApp, e aqui pareceria uma só.
                Texto = null,
                TipoMidia = TipoMidia.Audio,
                MidiaChave = chave,
                MidiaMime = AudioOpus.MimeNotaDeVoz,
                MidiaNome = nome,
                MidiaBytes = ogg.Length,
                MidiaDuracaoSegundos = segundos,
                LembreteId = null,
                EnviadoPor = UsuarioAtual(),
                DataDisparo = DateOnly.FromDateTime(agora),
                ReservadoEm = agora
            };

            var (mensagemId, resultado) = await enviador.EnviarMidiaManualAsync(
                mensagem, conversa.Contato.Telefone,
                Convert.ToBase64String(ogg), AudioOpus.MimeNotaDeVoz, nome, null, ct);

            AtualizarConversaComSaida(conversa, $"🎤 Áudio · {segundos}s", agora);

            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);

            return await MontarRespostaAsync(mensagemId, resultado, ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public async Task<RespostaEnviada> ReenviarAsync(long mensagemId, CancellationToken ct)
    {
        // A MESMA LINHA, sempre. Criar outra duplicaria a mensagem para o cliente no caso em que
        // a Evolution recebeu e a resposta se perdeu — o modo de falha mais provavel de todos.
        var mensagem = await db.Mensagens
            .Include(m => m.Contato)
            .FirstOrDefaultAsync(m => m.Id == mensagemId, ct)
            ?? throw new RegraDeNegocioException("Mensagem não encontrada.");

        if (mensagem.Direcao != DirecaoMensagem.Saida)
            throw new RegraDeNegocioException("Só dá para reenviar mensagem que você mandou.");

        if (mensagem.EnviadaEm is not null)
            throw new RegraDeNegocioException("Esta mensagem já foi enviada.", conflito: true);

        var telefone = mensagem.Contato.Telefone;

        ResultadoEnvio resultado;
        if (mensagem.MidiaChave is { } chave)
        {
            var conteudo = await armazenamento.AbrirAsync(chave, ct)
                ?? throw new RegraDeNegocioException(
                    "O arquivo desta mensagem não está mais disponível. Envie de novo.");

            using var memoria = new MemoryStream();
            await conteudo.CopyToAsync(memoria, ct);

            resultado = await enviador.ReenviarMidiaAsync(
                mensagem, telefone, Convert.ToBase64String(memoria.ToArray()),
                mensagem.MidiaMime ?? "application/octet-stream",
                mensagem.MidiaNome ?? "arquivo", mensagem.Texto, ct);
        }
        else
        {
            resultado = await enviador.ReenviarAsync(mensagem, telefone, ct);
        }

        return await MontarRespostaAsync(mensagemId, resultado, ct);
    }

    // ---------------------------------------------------------------- apoio de envio
    private async Task<Conversa> CarregarParaEnvioAsync(long conversaId, CancellationToken ct)
    {
        var conversa = await db.Conversas
            .Include(c => c.Contato)
            .Include(c => c.Conexao)
            .FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        if (string.IsNullOrWhiteSpace(conversa.Contato.Telefone))
            throw new RegraDeNegocioException("Este contato não tem telefone — não dá para responder.");

        if (!await enviador.InstanciaConectadaAsync(conversa.Conexao.InstanceName, ct))
            throw new RegraDeNegocioException(
                "O WhatsApp está desconectado. Reconecte o número em Conexão e tente de novo.",
                conflito: true);

        return conversa;
    }

    private static void AtualizarConversaComSaida(Conversa conversa, string previa, DateTime agora)
    {
        conversa.AguardandoDesde = null;
        conversa.NaoLidas = 0;
        conversa.UltimaMensagemEm = agora;
        conversa.UltimaMensagemDirecao = DirecaoMensagem.Saida;
        conversa.UltimaMensagemPrevia = PreviaTexto.Cortar(previa);
    }

    /// <summary>A previa de um anexo SEM legenda. Deixar em branco faria a linha da caixa de
    /// entrada parecer vazia — e o vendedor nao saberia que respondeu.</summary>
    private static string PreviaDeAnexo(string mime, string nome) =>
        ValidadorMidia.TipoDe(mime) == TipoMidia.Imagem ? "📷 Imagem" : $"📎 {nome}";

    /// <summary>O nome que o WhatsApp vai mostrar. Vem do cliente, entao: sem caminho (um
    /// `../../etc/passwd` nao pode virar nome de arquivo em lugar nenhum), sem exagero de
    /// tamanho, e com extensao coerente com o que os BYTES dizem.</summary>
    private static string NomeSeguro(string? informado, string mime)
    {
        var extensao = ValidadorMidia.ExtensaoDe(mime);
        var bruto = Path.GetFileName(informado ?? "").Trim();

        if (bruto.Length == 0) return $"arquivo.{extensao}";
        if (bruto.Length > 120) bruto = bruto[..120];

        var semExtensao = Path.GetFileNameWithoutExtension(bruto);
        if (semExtensao.Length == 0) semExtensao = "arquivo";

        return $"{semExtensao}.{extensao}";
    }

    private async Task<RespostaEnviada> MontarRespostaAsync(
        long mensagemId, ResultadoEnvio resultado, CancellationToken ct)
    {
        var erro = resultado == ResultadoEnvio.Enviada
            ? null
            : await db.Mensagens.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.Id == mensagemId).Select(m => m.Erro).FirstOrDefaultAsync(ct);

        return new RespostaEnviada(mensagemId, resultado == ResultadoEnvio.Enviada, erro);
    }

    public async Task AssumirAsync(long conversaId, CancellationToken ct)
    {
        var conversa = await db.Conversas.FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        var meuId = UsuarioAtual();
        if (conversa.ResponsavelId is { } dono && dono != meuId)
            throw new RegraDeNegocioException(
                "Esta conversa já está sendo atendida por outro vendedor.", conflito: true);

        if (conversa.ResponsavelId == meuId) return;   // reassumir a propria: no-op

        // ===================== A EXCEÇÃO DA CONVERSA (AUD-1) =====================
        // `conversas` NÃO é auditada no fluxo normal: ela é escrita a cada mensagem
        // (`aguardando_desde`, `nao_lidas`, `ultima_mensagem_*`), e auditar isso geraria mais
        // linha de trilha que de mensagem, sem utilidade nenhuma.
        //
        // Assumir e liberar são o oposto: DECISÃO HUMANA, e a pergunta "quem pegou esse
        // atendimento" é exatamente o tipo de coisa que a trilha existe para responder.
        // =========================================================================
        trilha.Declarar(EntidadeAuditada.Conversa, conversa.Id, AcaoAuditoria.Atribuiu);

        conversa.ResponsavelId = meuId;
        conversa.AtribuidoEm = relogio.GetUtcNow().UtcDateTime;

        // ===================== QUEM ATENDE FICA COM O LEAD =====================
        // `contatos.responsavel_id` e `conversas.responsavel_id` sao a MESMA ideia guardada em
        // dois lugares, e so a segunda era escrita pelo fluxo vivo. Quem digita o contato a mao
        // preenche a primeira pelo formulario; quem chega pelo WhatsApp — ou seja, todo lead de
        // verdade — nunca preenchia nenhuma, porque a unica atribuicao que acontece e este botao.
        //
        // O sintoma era a coluna "Responsavel" da lista de contatos vindo vazia. Mas quem le
        // `contatos.responsavel_id` sao QUATRO telas: a lista, o card do kanban, o filtro por
        // responsavel e o Meu Dia. As quatro diziam "sem responsavel" para lead com dono ha
        // semanas, e nada denunciava porque o semeador preenche a coluna do contato — a
        // demonstracao parecia certa.
        //
        // O proprio semeador ja documentava a invariante que faltava: ele copia
        // `conversa.ResponsavelId = contato.ResponsavelId`. As duas andam juntas.
        //
        // ⚠️ SO PREENCHE O QUE ESTA VAGO. Um gestor pode ter atribuido o contato a alguem pelo
        // formulario; assumir a conversa e dizer "eu atendo", nao "o lead virou meu". Sobrescrever
        // faria o primeiro a responder roubar a carteira do colega, em silencio.
        await AtribuirContatoSeVagoAsync(conversa.ContatoId, meuId, ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Poe o dono no contato quando ele nao tem nenhum. `ExecuteUpdate` com o predicado
    /// `responsavel_id IS NULL` no WHERE, e nao uma leitura seguida de escrita: e o que torna a
    /// operacao segura contra dois vendedores clicando ao mesmo tempo — o segundo afeta zero
    /// linhas em vez de sobrescrever o primeiro.</summary>
    private Task AtribuirContatoSeVagoAsync(long contatoId, long? meuId, CancellationToken ct) =>
        db.Contatos
            .Where(c => c.Id == contatoId && c.ResponsavelId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, meuId), ct);

    public async Task LiberarAsync(long conversaId, CancellationToken ct)
    {
        var conversa = await db.Conversas.FirstOrDefaultAsync(c => c.Id == conversaId, ct)
            ?? throw new RegraDeNegocioException("Conversa não encontrada.");

        var meuId = UsuarioAtual();
        if (conversa.ResponsavelId is { } dono && dono != meuId)
            throw new RegraDeNegocioException(
                "Só quem está atendendo pode liberar a conversa.", conflito: true);

        trilha.Declarar(EntidadeAuditada.Conversa, conversa.Id, AcaoAuditoria.Atribuiu);
        conversa.ResponsavelId = null;
        conversa.AtribuidoEm = null;

        // Solta o lead junto — mas SO se for de quem esta liberando. Deixar o contato no nome de
        // quem saiu faria a lista e o kanban apontarem para o vendedor errado; soltar o de outro
        // seria o roubo ao contrario.
        await db.Contatos
            .Where(c => c.Id == conversa.ContatoId && c.ResponsavelId == meuId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResponsavelId, (long?)null), ct);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>0 fora de requisicao autenticada — nao deveria acontecer aqui (todo caminho e
    /// [Authorize]), mas NULL e melhor que gravar 0 numa FK.</summary>
    private long? UsuarioAtual() => contexto.UsuarioId == 0 ? null : contexto.UsuarioId;
}
