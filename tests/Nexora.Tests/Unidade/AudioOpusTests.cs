using Nexora.Core.Whatsapp;

namespace Nexora.Tests.Unidade;

/// <summary>BLOCO 13 — o formato da nota de voz.
///
/// ===================== POR QUE ISTO PRECISA DE TESTE =====================
/// O sintoma de errar aqui não é um erro: é a mensagem chegando como ARQUIVO ANEXO em vez de nota
/// de voz. O cliente vê um clipe para baixar, o vendedor acha que deu certo, e ninguém abre
/// chamado. Um `dotnet test` verde com o formato errado seria pior que teste nenhum.
///
/// O WebM aqui é MONTADO À MÃO, byte a byte: é a única forma de ter um arquivo determinístico sem
/// depender de um navegador nem versionar um binário no repositório.
/// =========================================================================</summary>
public class AudioOpusTests
{
    // ==================================================================== construção do WebM
    /// <summary>Tamanho EBML, com a LARGURA CERTA.
    ///
    /// ⚠️ A primeira versão era `(byte)(0x80 | n)` — que só funciona até 127. Um cluster de 2350
    /// bytes virava 126, o parser lia até o meio e continuava do lugar errado; o teste acusava
    /// menos pacotes e eu quase fui procurar o defeito no parser, que estava certo.
    ///
    /// Largura N: o marcador é o bit (8 - N) do primeiro byte, e o valor ocupa os 7N bits
    /// restantes.</summary>
    private static byte[] Tam(int n)
    {
        for (var largura = 1; largura <= 4; largura++)
        {
            var maximo = (1 << (7 * largura)) - 1;
            if (n >= maximo) continue;

            var bytes = new byte[largura];
            var v = (long)n;
            for (var k = largura - 1; k >= 0; k--) { bytes[k] = (byte)(v & 0xFF); v >>= 8; }
            bytes[0] |= (byte)(0x80 >> (largura - 1));
            return bytes;
        }
        throw new ArgumentOutOfRangeException(nameof(n));
    }

    /// <summary>Um vint de UM byte, para o número da faixa dentro do SimpleBlock.</summary>
    private static byte Vint1(int n) => (byte)(0x80 | n);

    private static byte[] Elemento(byte[] id, byte[] conteudo) =>
        [.. id, .. Tam(conteudo.Length), .. conteudo];

    /// <summary>Um pacote de Opus com TOC escolhido: config 16 (CELT), quadro de 20 ms, 1 quadro.
    ///
    /// config 16 -> 16 % 4 = 0 -> 120 amostras... então uso config 19 (19 % 4 = 3 -> 960
    /// amostras = 20 ms em 48 kHz), que é o que um codificador de voz produz.</summary>
    private static byte[] PacoteOpus(int corpo = 40)
    {
        const int config = 19;
        var toc = (byte)((config << 3) | 0);   // código 0 = 1 quadro por pacote
        return [toc, .. Enumerable.Repeat((byte)0x55, corpo)];
    }

    /// <summary>Um WebM mínimo mas ESTRUTURALMENTE REAL: cabeçalho EBML, Segment > Tracks >
    /// TrackEntry > CodecPrivate, e Segment > Cluster > SimpleBlock por pacote.</summary>
    private static byte[] Webm(int pacotes, byte[]? codecPrivate = null)
    {
        var opusHead = codecPrivate
            ?? [.. "OpusHead"u8, 1, 1, 0x38, 0x01, 0x80, 0xBB, 0x00, 0x00, 0x00, 0x00, 0];

        var trackEntry = Elemento([0xAE], Elemento([0x63, 0xA2], opusHead));
        var tracks = Elemento([0x16, 0x54, 0xAE, 0x6B], trackEntry);

        var blocos = new List<byte>();
        for (var i = 0; i < pacotes; i++)
        {
            // SimpleBlock: número da faixa (vint), timecode (2 bytes), flags (1), dados.
            byte[] corpo = [Vint1(1), 0x00, 0x00, 0x80, .. PacoteOpus()];
            blocos.AddRange(Elemento([0xA3], corpo));
        }
        var cluster = Elemento([0x1F, 0x43, 0xB6, 0x75], [.. blocos]);

        var segment = Elemento([0x18, 0x53, 0x80, 0x67], [.. tracks, .. cluster]);
        byte[] ebml = [0x1A, 0x45, 0xDF, 0xA3, .. Tam(1), 0x00];

        return [.. ebml, .. segment];
    }

    // ==================================================================== detecção
    [Fact]
    public void Reconhece_OGG_e_WEBM_pelos_bytes_iniciais()
    {
        Assert.True(AudioOpus.EhOgg("OggS"u8.ToArray()));
        Assert.True(AudioOpus.EhWebm([0x1A, 0x45, 0xDF, 0xA3, 0x00]));

        // MP4 do Safari: nem um nem outro — e é isso que faz a gravação ser recusada lá com
        // mensagem clara, em vez de virar um anexo que o cliente não ouve.
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, .. "ftypmp42"u8];
        Assert.False(AudioOpus.EhOgg(mp4));
        Assert.False(AudioOpus.EhWebm(mp4));
        Assert.Null(AudioOpus.ParaNotaDeVoz(mp4));
    }

    [Fact]
    public void OGG_passa_DIRETO_sem_ser_reempacotado()
    {
        // Firefox já grava OGG/Opus. Remuxar o que já está certo só criaria uma chance a mais de
        // estragar — e o teste fixa que os bytes são os MESMOS, não equivalentes.
        var ogg = AudioOpus.RemuxarWebmParaOgg(Webm(3))!;
        Assert.Same(ogg, AudioOpus.ParaNotaDeVoz(ogg));
    }

    // ==================================================================== remux
    [Fact]
    public void REMUX_PRODUZ_UM_OGG_COM_A_ESTRUTURA_QUE_O_FORMATO_EXIGE()
    {
        var ogg = AudioOpus.RemuxarWebmParaOgg(Webm(5))!;

        Assert.True(AudioOpus.EhOgg(ogg));

        // Os DOIS cabeçalhos obrigatórios. Sem o OpusTags o decodificador recusa o fluxo — e o
        // sintoma seria "o áudio não toca", sem nada dizendo por quê.
        Assert.Contains("OpusHead", System.Text.Encoding.ASCII.GetString(ogg));
        Assert.Contains("OpusTags", System.Text.Encoding.ASCII.GetString(ogg));

        var paginas = Paginas(ogg);
        Assert.True(paginas.Count >= 3, "OpusHead, OpusTags e ao menos uma de áudio");

        // A primeira página é INÍCIO DE FLUXO (0x02) e a última é FIM (0x04). Sem essas marcas o
        // WhatsApp trata o arquivo como truncado.
        Assert.Equal(0x02, paginas[0].Tipo);
        Assert.Equal(0x04, paginas[^1].Tipo);

        // Numeração sequencial, sem buraco: página fora de ordem faz o player parar no meio.
        for (var i = 0; i < paginas.Count; i++) Assert.Equal(i, paginas[i].Seq);
    }

    [Fact]
    public void O_CRC_DE_CADA_PAGINA_CONFERE()
    {
        // ⚠️ O CRC do OGG NÃO é o CRC-32 comum: mesmo polinômio, mas sem reflexão de bits e sem
        // XOR final. Usar o do zip produz arquivo que todo player recusa — e a mensagem de erro
        // fala em "corrompido", não em CRC. Este teste recalcula com a definição do formato.
        var ogg = AudioOpus.RemuxarWebmParaOgg(Webm(4))!;

        foreach (var p in Paginas(ogg))
        {
            var copia = ogg[p.Inicio..p.Fim];
            var declarado = BitConverter.ToUInt32(copia, 22);
            Array.Clear(copia, 22, 4);          // o CRC é calculado com o campo ZERADO

            Assert.Equal(declarado, CrcOgg(copia));
        }
    }

    [Fact]
    public void A_DURACAO_SAI_DA_GRANULE_POSITION_e_bate_com_os_pacotes()
    {
        // Cada pacote do fixture é 1 quadro de 20 ms. 50 pacotes = 1 segundo redondo.
        var ogg = AudioOpus.RemuxarWebmParaOgg(Webm(50))!;

        var d = AudioOpus.DuracaoDe(ogg);
        Assert.NotNull(d);

        // O pre-skip do OpusHead do fixture (0x0138 = 312 amostras) é descontado: são amostras de
        // aquecimento do decodificador, não som.
        var esperado = (50 * 960 - 312) / 48000.0;
        Assert.Equal(esperado, d!.Value.TotalSeconds, precision: 4);
    }

    [Fact]
    public void Duracao_de_arquivo_que_nao_e_OGG_devolve_null()
    {
        Assert.Null(AudioOpus.DuracaoDe([1, 2, 3]));
        Assert.Null(AudioOpus.DuracaoDe(Webm(2)));   // WebM não é OGG
    }

    [Fact]
    public void WEBM_SEM_PACOTE_E_RECUSADO_em_vez_de_virar_OGG_vazio()
    {
        // Um OGG sem áudio é aceito pela Evolution e chega como uma nota de voz de 0 segundo.
        // Recusar aqui é o que transforma isso em erro visível para o vendedor.
        Assert.Null(AudioOpus.RemuxarWebmParaOgg(Webm(0)));
        Assert.Null(AudioOpus.ParaNotaDeVoz([0x1A, 0x45, 0xDF, 0xA3]));
    }

    [Fact]
    public void Arquivo_truncado_nao_estoura_e_e_recusado()
    {
        var inteiro = Webm(6);
        for (var corte = 6; corte < inteiro.Length; corte += 7)
        {
            var pedaco = inteiro[..corte];
            var r = AudioOpus.RemuxarWebmParaOgg(pedaco);   // não pode lançar
            if (r is not null) Assert.True(AudioOpus.EhOgg(r));
        }
    }

    [Fact]
    public void SEM_CodecPrivate_monta_um_OpusHead_padrao()
    {
        // Nem todo MediaRecorder escreve CodecPrivate. Sem OpusHead o arquivo não é tocável, e
        // desistir seria pior que assumir o padrão de 48 kHz mono que os codificadores usam.
        var webm = Webm(3, codecPrivate: []);
        var ogg = AudioOpus.RemuxarWebmParaOgg(webm);

        Assert.NotNull(ogg);
        Assert.Contains("OpusHead", System.Text.Encoding.ASCII.GetString(ogg!));
    }

    [Fact]
    public void O_teto_de_duracao_e_de_CINCO_MINUTOS()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), AudioOpus.DuracaoMaxima);
    }

    // ==================================================================== apoio
    private sealed record Pagina(int Inicio, int Fim, byte Tipo, int Seq);

    /// <summary>Varre as páginas do OGG lendo a tabela de segmentos — a mesma leitura que um
    /// player faz. Se o remux escrever a tabela errada, isto desanda.</summary>
    private static List<Pagina> Paginas(byte[] ogg)
    {
        var lista = new List<Pagina>();
        var i = 0;

        while (i + 27 <= ogg.Length && ogg[i] == 'O' && ogg[i + 1] == 'g'
               && ogg[i + 2] == 'g' && ogg[i + 3] == 'S')
        {
            var segmentos = ogg[i + 26];
            var dados = 0;
            for (var k = 0; k < segmentos; k++) dados += ogg[i + 27 + k];

            var fim = i + 27 + segmentos + dados;
            lista.Add(new Pagina(i, fim, ogg[i + 5], BitConverter.ToInt32(ogg, i + 18)));
            i = fim;
        }
        return lista;
    }

    /// <summary>O CRC do OGG, reimplementado a partir da ESPECIFICAÇÃO — não copiado do código
    /// que está sendo testado. Copiar faria os dois errarem juntos em silêncio.</summary>
    private static uint CrcOgg(ReadOnlySpan<byte> dados)
    {
        uint crc = 0;
        foreach (var b in dados)
        {
            crc ^= (uint)b << 24;
            for (var i = 0; i < 8; i++)
                crc = (crc & 0x8000_0000) != 0 ? (crc << 1) ^ 0x04C1_1DB7 : crc << 1;
        }
        return crc;
    }
}
